using System.Buffers.Binary;

namespace PakuPort.Core;

/// <summary>
/// Loader LE (Linear Executable) de 32 bits.
///
/// Un LE es lo que produce el enlazador de Watcom para correr bajo un extensor DOS (DOS/4GW y
/// compañía): el `.EXE` lleva delante un stub MZ que sólo sirve para lanzar el extensor, y
/// detrás la imagen real, ya de 32 bits, en objetos con base propia dentro de un espacio PLANO.
/// PakuPort no ejecuta el extensor: carga el LE él mismo y le da los servicios, igual que hace
/// con el DOS.
///
/// **El código de un LE no contiene la transición a modo protegido** — eso lo hacía el extensor
/// —, así que aquí no hay nada que emular: se coloca la imagen y se arranca en su EIP con el
/// intérprete ya en 32 bits.
///
/// Lo que este loader NO hace, y lanza en vez de callar: páginas iteradas o comprimidas, fixups
/// con lista de origen, fixups aditivos, alias 16:16 e importaciones. Nada de eso aparece en el
/// binario objetivo (medido: `tools/le_loader/README.md`), y un cargador que los ignore en
/// silencio produce una imagen que arranca y descarrila mil instrucciones después.
///
/// <b>Layout: los objetos van PEGADOS, no en la base que pide el fichero.</b> Esto NO se dedujo,
/// se midió contra DOS/4GW en el oráculo (`tools/le_loader/judge_le.py`): la tabla de objetos de
/// Un juego de prueba pide 0x10000 y 0x70000 —15 páginas de hueco entre medias— y el extensor coloca el
/// segundo justo detrás del primero. Aplicar las bases del fichero deja 33.048 bytes distintos
/// del oráculo, todos en sitios de fixup; con los objetos pegados quedan DOS, y esos dos los
/// escribe el propio juego al arrancar. Los fixups internos son direcciones lineales
/// ABSOLUTAS del espacio ya colocado, no offsets relativos a nada.
/// </summary>
public sealed class LeLoader
{
    /// <param name="Base">Dónde queda el objeto DE VERDAD, ya colocado.</param>
    /// <param name="FileBase">La base que pide la tabla de objetos del fichero. NO es donde
    /// acaba: el extensor empaqueta los objetos pegados y se come el hueco (ver §Layout).</param>
    public sealed record Obj(int Number, uint Base, uint FileBase, uint VirtualSize, uint Flags,
                             int PageIndex, int PageCount)
    {
        public bool Readable => (Flags & 0x01) != 0;
        public bool Writable => (Flags & 0x02) != 0;
        public bool Executable => (Flags & 0x04) != 0;
        /// <summary>Flag BIG: el segmento es de 32 bits por defecto. OJO: el "alias 16:16" es
        /// el 0x1000, no éste.</summary>
        public bool Is32Bit => (Flags & 0x2000) != 0;
    }

    /// <summary>Imagen plana desde la dirección 0. Los huecos entre objetos y el BSS (la parte
    /// del objeto que no tiene páginas en el fichero) quedan a cero, como los deja el extensor.</summary>
    public byte[] Image { get; private set; } = System.Array.Empty<byte>();
    public uint Eip { get; private set; }
    public uint Esp { get; private set; }
    public List<Obj> Objects { get; } = new();
    /// <summary>Fixups aplicados. Telemetría del juez: si este número baila entre versiones del
    /// loader es que se está tragando registros.</summary>
    public int FixupCount { get; private set; }

    const int SrcSel16 = 0x02, SrcOff16 = 0x05, SrcPtr1632 = 0x06, SrcOff32 = 0x07, SrcRel32 = 0x08;
    const int SrcAlias = 0x10, SrcList = 0x20;
    const int TgtMask = 0x03, TgtInternal = 0x00;
    const int TgtAdditive = 0x04, TgtOff32 = 0x10, TgtAdd32 = 0x20, TgtObj16 = 0x40;

    public static bool IsLe(byte[] exe)
    {
        if (exe.Length < 0x40 || exe[0] != 'M' || exe[1] != 'Z') return false;
        int o = (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x3C));
        return o > 0 && o + 2 <= exe.Length && exe[o] == 'L' && exe[o + 1] == 'E';
    }

    /// <summary>Base lineal donde se coloca el primer objeto. Bajo DOS/4GW la elige el extensor
    /// según la memoria libre (en la medida del oráculo salió 0x1E6000, y no es estable de una
    /// ejecución a otra); aquí la elige PakuPort, que para eso sustituye al extensor. El juez
    /// pasa la del oráculo para poder comparar byte a byte.</summary>
    public uint LoadBase { get; private set; }

    public static LeLoader Load(byte[] exe, uint loadBase = 0)
    {
        if (!IsLe(exe)) throw new InvalidOperationException("no es un ejecutable LE");
        int o = (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(0x3C));

        uint U32(int off) => BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(o + off));

        int numPages     = (int)U32(0x14);
        int eipObject    = (int)U32(0x18);
        uint eip         = U32(0x1C);
        int espObject    = (int)U32(0x20);
        uint esp         = U32(0x24);
        int pageSize     = (int)U32(0x28);
        int lastPageSize = (int)U32(0x2C);   // en LE: bytes útiles de la ÚLTIMA página
        int objTable     = o + (int)U32(0x40);
        int numObjects   = (int)U32(0x44);
        int pageMap      = o + (int)U32(0x48);
        int fixupPages   = o + (int)U32(0x68);
        int fixupRecords = o + (int)U32(0x6C);
        int dataPages    = (int)U32(0x80);   // offset ABSOLUTO en el fichero, no relativo a la cabecera

        var le = new LeLoader { LoadBase = loadBase };

        // Los objetos se colocan PEGADOS desde loadBase, cada uno alineado a página. La base que
        // trae la tabla del fichero se guarda aparte porque NO es donde acaban (ver el comentario
        // de la clase: medido contra DOS/4GW).
        long cursor = loadBase;
        for (int i = 0; i < numObjects; i++)
        {
            int b = objTable + i * 24;
            uint vsize = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(b));
            int pcnt = (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(b + 16));
            le.Objects.Add(new Obj(
                i + 1,
                Base: checked((uint)cursor),
                FileBase: BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(b + 4)),
                VirtualSize: vsize,
                Flags: BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(b + 8)),
                PageIndex: (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(b + 12)),
                PageCount: pcnt));
            // Se reserva el tamaño VIRTUAL (páginas del fichero + BSS), redondeado a página.
            // OJO, honestidad: en este binario `roundup(vsize)` y `páginas*4096` valen lo mismo
            // para el objeto 1 (0x51000 los dos), así que la medida del oráculo NO distingue las
            // dos reglas. Se usa el tamaño virtual porque es el que el objeto necesita de verdad:
            // el BSS del objeto 2 son 116 KB que tienen que existir.
            long reservado = Math.Max(vsize, (long)pcnt * pageSize);
            cursor += (reservado + pageSize - 1) / pageSize * pageSize;
        }
        if (le.Objects.Count == 0) throw new InvalidOperationException("LE sin objetos");

        long top = 0;
        foreach (var ob in le.Objects) top = Math.Max(top, (long)ob.Base + ob.VirtualSize);
        if (top > int.MaxValue) throw new InvalidOperationException($"imagen LE descomunal ({top} bytes)");
        le.Image = new byte[top];

        // --- páginas. El mapa es GLOBAL (1..numPages) y cada objeto se queda un tramo de él.
        // Entrada de 4 bytes: número de página en 3 (los dos altos BIG-endian) + flags.
        foreach (var ob in le.Objects)
        {
            for (int k = 0; k < ob.PageCount; k++)
            {
                int e = pageMap + (ob.PageIndex - 1 + k) * 4;
                int pageNum = (BinaryPrimitives.ReadUInt16BigEndian(exe.AsSpan(e)) << 8) | exe[e + 2];
                int pflags = exe[e + 3];
                long dst = ob.Base + (long)k * pageSize;
                if (pflags == 3) continue;      // rellena de ceros: ya lo está
                if (pflags != 0)
                    throw new NotSupportedException(
                        $"LE: página {pageNum} con flags={pflags} (iterada/inválida) no soportada");

                int src = dataPages + (pageNum - 1) * pageSize;
                int n = (pageNum == numPages && lastPageSize > 0) ? lastPageSize : pageSize;
                n = Math.Min(n, pageSize);
                n = (int)Math.Min(n, exe.Length - (long)src);
                n = (int)Math.Min(n, le.Image.Length - dst);
                if (n > 0) System.Array.Copy(exe, src, le.Image, dst, n);
            }
        }

        // --- fixups ---
        var pageAddr = new Dictionary<int, uint>();
        var objBase = new Dictionary<int, uint>();
        foreach (var ob in le.Objects)
        {
            objBase[ob.Number] = ob.Base;
            for (int k = 0; k < ob.PageCount; k++)
                pageAddr[ob.PageIndex + k] = (uint)(ob.Base + (long)k * pageSize);
        }

        for (int pg = 1; pg <= numPages; pg++)
        {
            int a = fixupRecords + (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(fixupPages + (pg - 1) * 4));
            int end = fixupRecords + (int)BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(fixupPages + pg * 4));
            if (!pageAddr.TryGetValue(pg, out uint pbase))
            {
                if (a != end) throw new InvalidOperationException($"LE: la página {pg} tiene fixups y no pertenece a ningún objeto");
                continue;
            }

            int i = a;
            while (i < end)
            {
                byte st = exe[i], fl = exe[i + 1];
                i += 2;
                int kind = st & 0x0F;
                if ((st & SrcAlias) != 0)
                    throw new NotSupportedException($"LE: fixup con alias 16:16 (st=0x{st:X2}) no soportado");

                int count = 1;
                short[] srcOffs;
                if ((st & SrcList) != 0)
                {
                    count = exe[i]; i += 1;
                    srcOffs = System.Array.Empty<short>();   // la lista viene al final del registro
                }
                else
                {
                    srcOffs = new[] { BinaryPrimitives.ReadInt16LittleEndian(exe.AsSpan(i)) };
                    i += 2;
                }

                if ((fl & TgtMask) != TgtInternal)
                    throw new NotSupportedException(
                        $"LE: fixup no interno (fl=0x{fl:X2}); este loader no resuelve importaciones");

                int tobj;
                if ((fl & TgtObj16) != 0) { tobj = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(i)); i += 2; }
                else { tobj = exe[i]; i += 1; }

                uint toff = 0;
                if (kind != SrcSel16)
                {
                    if ((fl & TgtOff32) != 0) { toff = BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(i)); i += 4; }
                    else { toff = BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(i)); i += 2; }
                }

                if ((fl & TgtAdditive) != 0)
                {
                    if ((fl & TgtAdd32) != 0) { toff += BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(i)); i += 4; }
                    else { toff += BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(i)); i += 2; }
                }

                if ((st & SrcList) != 0)
                {
                    srcOffs = new short[count];
                    for (int k = 0; k < count; k++)
                        srcOffs[k] = BinaryPrimitives.ReadInt16LittleEndian(exe.AsSpan(i + 2 * k));
                    i += 2 * count;
                }

                if (!objBase.TryGetValue(tobj, out uint tbase))
                    throw new InvalidOperationException($"LE: fixup a un objeto inexistente ({tobj})");
                uint target = tbase + toff;

                foreach (short so in srcOffs)
                {
                    // Un offset de origen NEGATIVO significa que el campo empieza en la página
                    // ANTERIOR y cruza el borde; el destino no cambia.
                    long addr = pbase + so;
                    le.ApplyFixup(kind, addr, target, tobj);
                }
            }
            if (i != end)
                throw new InvalidOperationException(
                    $"LE: la tabla de fixups de la página {pg} no se consume exacta ({i} != {end}); " +
                    "el modelo del formato está mal y la imagen sería basura plausible");
        }

        if (!objBase.TryGetValue(eipObject, out uint eBase) || !objBase.TryGetValue(espObject, out uint sBase))
            throw new InvalidOperationException("LE: entrada o pila apuntan a un objeto inexistente");
        le.Eip = eBase + eip;
        le.Esp = sBase + esp;
        return le;
    }

    void ApplyFixup(int kind, long addr, uint target, int targetObject)
    {
        var m = Image;
        switch (kind)
        {
            case SrcSel16:
            {
                // Fixup de SELECTOR de 16 bits: el campo NO lleva offset (por eso el parser no lo
                // lee) — sólo hay que escribir el selector del objeto destino. En el mundo plano
                // que PakuPort sustituye a DOS/4GW hay exactamente dos, con base 0 los dos: el de
                // código y el de datos (Platform.FlatServices). Se elige por el flag EXECUTABLE
                // del objeto al que apunta el fixup, que es lo que hace el extensor real.
                if (addr < 0 || addr + 2 > m.Length) return;
                var obj = Objects.Find(x => x.Number == targetObject);
                ushort sel = obj is { Executable: true }
                    ? Platform.FlatServices.SelCodigo
                    : Platform.FlatServices.SelDatos;
                BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan((int)addr), sel);
                break;
            }
            case SrcOff32:
                if (addr < 0 || addr + 4 > m.Length) return;
                BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan((int)addr), target);
                break;
            case SrcRel32:
                // autorrelativo: se guarda destino - (dirección del campo + 4)
                if (addr < 0 || addr + 4 > m.Length) return;
                BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan((int)addr), (int)(target - (addr + 4)));
                break;
            case SrcOff16:
                if (addr < 0 || addr + 2 > m.Length) return;
                BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan((int)addr), (ushort)target);
                break;
            case SrcPtr1632:
                // 16:32 = offset de 32 bits + selector de 16. El selector lo asigna el host DPMI
                // en tiempo de carga (fase 2); aquí sólo se fija el offset.
                if (addr < 0 || addr + 6 > m.Length) return;
                BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan((int)addr), target);
                break;
            default:
                throw new NotSupportedException($"LE: tipo de origen de fixup 0x{kind:X2} no soportado");
        }
        FixupCount++;
    }
}
