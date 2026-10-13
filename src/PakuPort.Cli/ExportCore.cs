/// <summary>
/// Los ficheros de `src/PakuPort.Core` que se copian a CADA export, y las transformaciones que
/// hay que hacerles por el camino (quitar la dependencia del juez de casos, PakuPort.Judge, que no viaja).
///
/// Vive en una clase propia y no como funcion local de `Program` por una razon medida: la lista
/// tiene que ser UNA. Cuando se duplico —`Platform/FlatServices.cs` empezo a referenciar a
/// `Interp32` y este no estaba en la lista— los 21 exports dejaron de compilar y no lo vio
/// ningun gate, porque los gates compilan `src/` y no los exports. Ahora la comparten las dos
/// plantillas de host y el kit de remakes, que necesita refrescar el Core del export antes de
/// grabar: medir con un motor viejo es la clase de fallo silencioso que el kit existe para
/// evitar.
/// </summary>
static class ExportCore
{
    static void CopyIfExists(string src, string dst)
    {
        if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
    }


    public static void Copiar(string outDir)
    {
        string coreDir = Path.Combine(outDir, "Core");
        Directory.CreateDirectory(coreDir);
        string platformDir = Path.Combine(coreDir, "Platform");
        Directory.CreateDirectory(platformDir);

        string srcCore = Path.GetFullPath("src/PakuPort.Core");
        CopyIfExists(Path.Combine(srcCore, "Cpu.cs"), Path.Combine(coreDir, "Cpu.cs"));
        CopyIfExists(Path.Combine(srcCore, "Memory.cs"), Path.Combine(coreDir, "Memory.cs"));
        CopyIfExists(Path.Combine(srcCore, "Fpu.cs"), Path.Combine(coreDir, "Fpu.cs"));
        CopyIfExists(Path.Combine(srcCore, "Interp.cs"), Path.Combine(coreDir, "Interp.cs"));
        // El mundo plano de 32 bits. `Platform/FlatServices.cs` se copia con el resto de Platform y
        // referencia a `Interp32`, asi que sin estos dos el export NO COMPILA. Lo cazo la
        // recompilacion de los exports de los juegos de prueba, que es exactamente para lo que esta.
        CopyIfExists(Path.Combine(srcCore, "Interp32.cs"), Path.Combine(coreDir, "Interp32.cs"));
        CopyIfExists(Path.Combine(srcCore, "LeLoader.cs"), Path.Combine(coreDir, "LeLoader.cs"));
        CopyIfExists(Path.Combine(srcCore, "BlitProvenance.cs"), Path.Combine(coreDir, "BlitProvenance.cs"));
        CopyIfExists(Path.Combine(srcCore, "BlitRawLog.cs"), Path.Combine(coreDir, "BlitRawLog.cs"));
        CopyIfExists(Path.Combine(srcCore, "AudioEventLog.cs"), Path.Combine(coreDir, "AudioEventLog.cs"));
        CopyIfExists(Path.Combine(srcCore, "InputTape.cs"), Path.Combine(coreDir, "InputTape.cs"));
        CopyIfExists(Path.Combine(srcCore, "CorpusRecorder.cs"), Path.Combine(coreDir, "CorpusRecorder.cs"));
        CopyIfExists(Path.Combine(srcCore, "PerfilCapas.cs"), Path.Combine(coreDir, "PerfilCapas.cs"));
        CopyIfExists(Path.Combine(srcCore, "Cobertura.cs"), Path.Combine(coreDir, "Cobertura.cs"));
        CopyIfExists(Path.Combine(srcCore, "FuncionesPortadas.cs"), Path.Combine(coreDir, "FuncionesPortadas.cs"));
        CopyIfExists(Path.Combine(srcCore, "CorteDeterminista.cs"), Path.Combine(coreDir, "CorteDeterminista.cs"));
        CopyIfExists(Path.Combine(srcCore, "RastroDeterminismo.cs"), Path.Combine(coreDir, "RastroDeterminismo.cs"));
        CopyIfExists(Path.Combine(srcCore, "GrabadorFotogramas.cs"), Path.Combine(coreDir, "GrabadorFotogramas.cs"));
        // El contrato de llamada del remake: una funcion traducida llama con `Original` a lo que
        // todavia no lo esta, y eso lo sigue haciendo el binario original, interpretado.
        string verifyDir = Path.Combine(coreDir, "Verify");
        Directory.CreateDirectory(verifyDir);
        CopyIfExists(Path.Combine(srcCore, "Verify", "Original.cs"), Path.Combine(verifyDir, "Original.cs"));
        CopyIfExists(Path.Combine(srcCore, "RemakeMap.cs"), Path.Combine(coreDir, "RemakeMap.cs"));
        CopyIfExists(Path.Combine(srcCore, "EscaladoPixelArt.cs"), Path.Combine(coreDir, "EscaladoPixelArt.cs"));
        CopyIfExists(Path.Combine(srcCore, "EscaladoXbrz.cs"), Path.Combine(coreDir, "EscaladoXbrz.cs"));
        CopyIfExists(Path.Combine(srcCore, "Overlay.cs"), Path.Combine(coreDir, "Overlay.cs"));
        CopyIfExists(Path.Combine(srcCore, "MzLoader.cs"), Path.Combine(coreDir, "MzLoader.cs"));
        foreach (var f in Directory.GetFiles(Path.Combine(srcCore, "Platform"), "*.cs"))
            CopyIfExists(f, Path.Combine(platformDir, Path.GetFileName(f)));
        // Infraestructura de máquina (Ivt, Pic, Pit, BiosDataArea, Cmos, KeyboardController,
        // Speaker, Opl, MachineTimer) en la subcarpeta Platform/Machine.
        string srcMachine = Path.Combine(srcCore, "Platform", "Machine");
        if (Directory.Exists(srcMachine))
        {
            string machineDir = Path.Combine(platformDir, "Machine");
            Directory.CreateDirectory(machineDir);
            foreach (var f in Directory.GetFiles(srcMachine, "*.cs"))
                CopyIfExists(f, Path.Combine(machineDir, Path.GetFileName(f)));
        }

        // 2c-pre. Eliminar cualquier .csproj bajo Core/ (residuo de exports viejos):
        // el proyecto del port compila los .cs de Core por globbing SDK; un .csproj
        // anidado con refs a PakuPort.Judge/net10 rompería la portabilidad si se compilara.
        foreach (var stray in Directory.GetFiles(coreDir, "*.csproj", SearchOption.AllDirectories))
            File.Delete(stray);

        // 2c. Limpiar dependencias externas (PakuPort.Judge) de los archivos copiados
        foreach (var f in Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(f);
            var lines = content.Split('\n').ToList();
            // Eliminar using PakuPort.Judge y el método FromState que depende de CpuState
            lines.RemoveAll(l => l.TrimStart().StartsWith("using PakuPort.Judge"));
            // Eliminar el método FromState completo (depende de CpuState de PakuPort.Judge)
            int fromStateIdx = lines.FindIndex(l => l.Contains("FromState(in CpuState"));
            if (fromStateIdx >= 0)
            {
                int braces = 0; bool started = false;
                int endIdx = fromStateIdx;
                for (int i = fromStateIdx; i < lines.Count; i++)
                {
                    foreach (char ch in lines[i]) { if (ch == '{') { braces++; started = true; } else if (ch == '}') braces--; }
                    endIdx = i;
                    if (started && braces == 0) break;
                }
                lines.RemoveRange(fromStateIdx, endIdx - fromStateIdx + 1);
            }
            File.WriteAllText(f, string.Join('\n', lines));
        }
    }
}
