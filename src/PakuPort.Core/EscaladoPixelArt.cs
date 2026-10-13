using System;

namespace PakuPort.Core;

/// <summary>Ampliación de arte pixelado que respeta los bordes (algoritmo Scale3x).
///
/// Un juego de 320x240 estirado a una pantalla de hoy sale como bloques enormes si se amplía
/// repitiendo píxeles, y como una foto borrosa si se amplía interpolando. Scale3x hace otra cosa:
/// mira los ocho vecinos de cada píxel y, donde encuentra un borde diagonal, lo prolonga. Las
/// líneas quedan suaves y los colores planos siguen siendo planos — que es exactamente lo que
/// necesita un panel de instrumentos, una tipografía de mapa de bits o un sprite.
///
/// **Trabaja sobre ÍNDICES de paleta, no sobre RGB, y eso importa.** El algoritmo decide comparando
/// si dos píxeles son "el mismo color"; con RGB, dos índices distintos que el DAC pinta parecidos
/// se darían por iguales y se inventarían bordes que no existen. El índice ES la identidad que el
/// juego manejaba. (`VideoState.TryGetIndices` los da ya, de camino al renderizado.)
///
/// Es genérico: no sabe nada de ningún juego. El algoritmo es de dominio público
/// (scale2x.sourceforge.net, Andrea Mazzoleni).</summary>
public static class EscaladoPixelArt
{
    public const int Factor = 3;

    /// <summary>PAKU_SCALE3X=1: amplía el frame del juego con Scale3x antes de presentarlo.</summary>
    public static readonly bool Activo =
        Environment.GetEnvironmentVariable("PAKU_SCALE3X") == "1";

    /// <summary>Milisegundos del último escalado, para poder decidir si sale a cuenta.</summary>
    public static double UltimoCosteMs { get; private set; }

    static byte[]? _idx3, _rgba;

    /// <summary>Amplía x3 y devuelve RGBA listo para subir a una textura de (ancho*3, alto*3).
    /// <paramref name="paleta"/> son 3 bytes por índice.</summary>
    public static byte[] Amplia(byte[] idx, int ancho, int alto, byte[] paleta)
    {
        int w3 = ancho * Factor, h3 = alto * Factor;
        if (_idx3 == null || _idx3.Length != w3 * h3) _idx3 = new byte[w3 * h3];
        if (_rgba == null || _rgba.Length != w3 * h3 * 4) _rgba = new byte[w3 * h3 * 4];
        var o = _idx3;
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        for (int y = 0; y < alto; y++)
        {
            int arriba = y > 0 ? y - 1 : 0, abajo = y < alto - 1 ? y + 1 : alto - 1;
            for (int x = 0; x < ancho; x++)
            {
                int izq = x > 0 ? x - 1 : 0, der = x < ancho - 1 ? x + 1 : ancho - 1;
                // Los nueve vecinos, en la nomenclatura del algoritmo:  A B C / D E F / G H I
                byte a = idx[arriba * ancho + izq], b = idx[arriba * ancho + x], c = idx[arriba * ancho + der];
                byte d = idx[y * ancho + izq], e = idx[y * ancho + x], f = idx[y * ancho + der];
                byte g = idx[abajo * ancho + izq], h = idx[abajo * ancho + x], i = idx[abajo * ancho + der];

                byte e0 = e, e1 = e, e2 = e, e3 = e, e5 = e, e6 = e, e7 = e, e8 = e;
                // Sólo se toca donde hay un borde REAL: si los cuatro vecinos en cruz son iguales,
                // el píxel está en una zona plana y no hay nada que prolongar.
                if (d != f && b != h)
                {
                    if (d == b) e0 = d;
                    if ((d == b && e != c) || (b == f && e != a)) e1 = b;
                    if (b == f) e2 = f;
                    if ((h == d && e != a) || (d == b && e != g)) e3 = d;
                    if ((b == f && e != i) || (f == h && e != c)) e5 = f;
                    if (h == d) e6 = d;
                    if ((f == h && e != g) || (h == d && e != i)) e7 = h;
                    if (f == h) e8 = f;
                }

                int fila = y * Factor * w3 + x * Factor;
                o[fila] = e0; o[fila + 1] = e1; o[fila + 2] = e2;
                o[fila + w3] = e3; o[fila + w3 + 1] = e; o[fila + w3 + 2] = e5;
                o[fila + 2 * w3] = e6; o[fila + 2 * w3 + 1] = e7; o[fila + 2 * w3 + 2] = e8;
            }
        }

        var rgba = _rgba;
        int n = w3 * h3;
        for (int p = 0; p < n; p++)
        {
            int ci = o[p] * 3, po = p * 4;
            rgba[po] = paleta[ci]; rgba[po + 1] = paleta[ci + 1];
            rgba[po + 2] = paleta[ci + 2]; rgba[po + 3] = 255;
        }
        UltimoCosteMs = reloj.Elapsed.TotalMilliseconds;
        return rgba;
    }
}
