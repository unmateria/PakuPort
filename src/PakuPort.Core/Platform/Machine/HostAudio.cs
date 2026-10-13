namespace PakuPort.Core.Platform;

/// <summary>
/// Ring de audio PCM (mono, 16-bit) del host, mezclable y thread-safe. Lo escriben las fuentes
/// desde el HILO DEL JUEGO (SoundBlaster dentro de un OUT, OPL y Speaker desde MachineTimer) y
/// lo drena el HILO DE RENDER (que lo sube al AudioStream de Raylib). Es la ÚNICA pieza que no
/// se porta de DOSBox-X: su mixer es enorme; aquí basta un anillo con mezcla aditiva.
///
/// CADA FUENTE TIENE SU PROPIO CURSOR DE ESCRITURA (<see cref="Voice"/>), en posiciones
/// ABSOLUTAS. Así cada una ENCADENA sus bloques uno detrás de otro y las fuentes se MEZCLAN
/// entre sí donde coinciden en el tiempo.
///
/// El diseño anterior sumaba todo desde el frente de la ventana pendiente, apoyándose en que la
/// única fuente (el SB) escribía un bloque justo cuando el anterior se había drenado. En cuanto
/// una fuente generaba más a menudo que el drenaje (el speaker y el OPL generan cada pocos ms
/// desde el reloj, y el render consume en bloques de ~93 ms) los bloques se APILABAN sobre el
/// mismo tramo: amplitud multiplicada y onda destrozada — un tono de 1000 Hz salía a 125 Hz con
/// 6x de amplitud, medido por tools/speaker_test.
/// </summary>
public sealed class HostAudio
{
    /// <summary>Cursor de escritura de una fuente (SB, OPL, speaker...). Posición absoluta en
    /// muestras desde el arranque: no da la vuelta, así que no hay ambigüedad al comparar.</summary>
    public sealed class Voice
    {
        internal long WritePos;
    }

    private readonly short[] _ring;
    private readonly int _cap;
    private readonly object _lock = new();
    private long _readAbs;     // posición absoluta del cursor de reproducción
    private long _writeAbs;    // fin absoluto de lo escrito por cualquier fuente

    /// <summary>Frecuencia de muestreo del anillo/stream del host (Hz). El SB reamuestrea a ella.</summary>
    public int SampleRate { get; }

    // PAKU_LOG_SB=1: resumen por segundo del SUBDESBORDE del anillo (ver Read). Va con la misma
    // palanca que el resto del audio porque se leen juntos: el del SB dice si el invitado alimenta
    // el DMA, y éste dice si lo que se alimentó llega a sonar.
    static readonly bool _logSb = Environment.GetEnvironmentVariable("PAKU_LOG_SB") == "1";
    long _pedidas, _servidas, _faltas, _faltantes, _proximoAviso;

    public HostAudio(int sampleRate, int capacitySamples)
    {
        SampleRate = sampleRate;
        _cap = Math.Max(capacitySamples, 1);
        _ring = new short[_cap];
    }

    /// <summary>Crea un cursor de escritura para una fuente. Arranca en el frente de reproducción.</summary>
    public Voice CreateVoice()
    {
        lock (_lock) return new Voice { WritePos = _readAbs };
    }

    /// <summary>
    /// Suma <paramref name="n"/> muestras al anillo A PARTIR DEL CURSOR DE ESA FUENTE (con clamp
    /// a 16-bit) y avanza su cursor. Lo que otras fuentes ya escribieron en ese tramo se conserva
    /// y se mezcla. Una fuente que se quedó atrás (el render ya reprodujo su tramo) salta al
    /// frente en vez de escribir en el pasado; una que se adelanta más de lo que cabe en el
    /// anillo se recorta.
    /// </summary>
    public void AddSamples(Voice voice, short[] pcm, int n)
    {
        if (n <= 0) return;
        lock (_lock)
        {
            if (n > _cap) n = _cap;
            if (voice.WritePos < _readAbs) voice.WritePos = _readAbs;   // llegó tarde: al frente
            // Si escribir aquí desbordaría el anillo, tirar lo más viejo (sólo pasa si el render
            // se atasca; el ritmo de las fuentes lo marca el tiempo real).
            long end = voice.WritePos + n;
            if (end - _readAbs > _cap)
            {
                long drop = (end - _readAbs) - _cap;
                for (long i = 0; i < drop; i++) _ring[(int)((_readAbs + i) % _cap)] = 0;
                _readAbs += drop;
                if (voice.WritePos < _readAbs) voice.WritePos = _readAbs;
            }
            for (int i = 0; i < n; i++)
            {
                int pos = (int)((voice.WritePos + i) % _cap);
                _ring[pos] = (short)Math.Clamp(_ring[pos] + pcm[i], short.MinValue, short.MaxValue);
            }
            voice.WritePos += n;
            if (voice.WritePos > _writeAbs) _writeAbs = voice.WritePos;
        }
    }

    /// <summary>
    /// Muestras de esta fuente escritas y AÚN SIN REPRODUCIR (colchón por delante del cursor de
    /// lectura). Una fuente que entrega bloques mucho más largos que el anillo la usa para
    /// dosificarlos al ritmo real del drenaje en vez de volcarlos de golpe y perder la cola.
    /// </summary>
    public long Backlog(Voice voice)
    {
        lock (_lock) return Math.Max(0, voice.WritePos - _readAbs);
    }

    /// <summary>
    /// Drena hasta <paramref name="n"/> muestras a <paramref name="dst"/> (hilo de render).
    /// Deja a 0 las posiciones leídas (la mezcla aditiva parte de silencio).
    /// Devuelve cuántas muestras se han drenado (el resto de dst lo rellena el llamante).
    /// </summary>
    public int Read(short[] dst, int n)
    {
        lock (_lock)
        {
            int got = (int)Math.Min(n, _writeAbs - _readAbs);
            // TELEMETRÍA DEL SUBDESBORDE. Lo que el llamante hace cuando `got < n` es rellenar el
            // resto con SILENCIO, y el sub-buffer del host son 2048 muestras: un solo hueco mete
            // hasta 93 ms de nada a 22 kHz. Eso es "el sonido se entrecorta", y sin contarlo no se
            // distingue de "el juego no estaba sonando". El productor va al reloj del INVITADO
            // (avance del DMA) y el consumidor al reloj REAL: cualquier desfase vacía el anillo.
            _pedidas += n;
            _servidas += got;
            if (got < n) { _faltas++; _faltantes += n - got; }
            if (_logSb)
            {
                long ahora = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_proximoAviso == 0) _proximoAviso = ahora + System.Diagnostics.Stopwatch.Frequency;
                else if (ahora >= _proximoAviso)
                {
                    _proximoAviso = ahora + System.Diagnostics.Stopwatch.Frequency;
                    double msFaltantes = _faltantes * 1000.0 / SampleRate;
                    Console.Error.WriteLine(
                        $"[AUDIO] 1.00s: pedidas={_pedidas} servidas={_servidas} " +
                        $"huecos={_faltas} silencio_metido={msFaltantes:F1} ms " +
                        $"anillo={_writeAbs - _readAbs} muestras");
                    _pedidas = _servidas = _faltas = _faltantes = 0;
                }
            }
            for (int i = 0; i < got; i++)
            {
                int pos = (int)((_readAbs + i) % _cap);
                dst[i] = _ring[pos];
                _ring[pos] = 0;
            }
            _readAbs += got;
            return got;
        }
    }
}
