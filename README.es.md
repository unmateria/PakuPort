# PakuPort

**Ejecuta juegos de MS-DOS como programas .NET, y rehazlos función a función con un juez que
dice sí o no.**

PakuPort toma un ejecutable de DOS (`.EXE` o `.COM`, en modo real o en modo protegido de 32 bits)
y lo convierte en un proyecto .NET que ejecuta el juego: un intérprete x86 corre los bytes
originales sobre un PC emulado —DOS, BIOS, VGA/EGA/CGA, teclado, ratón, temporizador, altavoz,
Sound Blaster y OPL2—. Eso es el **port**.

Después te ayuda a sustituir el original, pieza a pieza, por código y arte tuyos. Eso es el
**remake**. Para cada función del juego escribe una carpeta con el desensamblado original y todo
lo que **midió** viendo correr el juego: la convención de llamada, las variables globales que
toca, a qué capa pertenece (dibujo, sonido, entrada o lógica) y cuánto de su código se ejecutó de
verdad. Tú escribes la función en C#, y un juez reproduce llamadas reales grabadas y compara
registros, banderas y memoria byte a byte. Verde o rojo, sin mirar a ojo. Luego tu traducción
puede enchufarse al juego en marcha, mientras todo lo que aún no has traducido lo sigue haciendo
el binario original.

Funciona **con IA o sin ella**. Las carpetas están escritas para que las lea una persona, y para
que un modelo de lenguaje trabaje con ellas a ciegas y aun así lo compruebe el juez, en vez de
creerle.

![El juego de ejemplo, porteado](docs/images/catch-ported.png)

*CATCH, el pequeño juego DOS original que viene en [`samples/catch`](samples/catch), corriendo
como programa .NET tras `pakuport port`.*

## Lo que hace bien

- **Ejecuta lo que ejecutaría la CPU, no lo que adivina un desensamblador.** En software de DOS,
  distinguir el código de los datos es indecidible: tablas de saltos, código automodificable,
  overlays, ejecutables comprimidos, protecciones que se descifran a sí mismas al arrancar.
  PakuPort no intenta adivinarlo: interpreta los bytes originales, así que todo eso simplemente
  funciona.
- **Juegos de 16 y de 32 bits.** Modo real, modo protegido de 16 bits, modo plano de 32 bits con
  DOS/4GW y otros extensores (también los que van metidos dentro del juego), paginación, VESA, y
  juegos que saltan una y otra vez entre código de 16 y de 32 bits.
- **Apunta lo que vio, no lo que supone.** Como el juego corre bajo su control, PakuPort puede
  grabar cada llamada a cada función con lo que entró y lo que salió, qué código se ejecutó de
  verdad, quién pintó cada píxel y quién hizo sonar cada sonido. Un desensamblador sólo puede
  ofrecer conjeturas; esto es el registro de lo que pasó.
- **Un juez, no una opinión.** Tu versión en C# de una función se repite contra llamadas reales
  grabadas y se compara con el original byte a byte: registros, banderas, memoria y hasta el
  número de instrucciones. Verde o rojo. Si ni el binario original reproduce un caso, el juez lo
  declara *injuzgable* en vez de echarte la culpa.
- **Pieza a pieza, con el juego en marcha.** Una función traducida se enchufa al juego mientras
  corre, y todo lo que aún no has traducido lo sigue haciendo el binario original. No hace falta
  tener el remake entero para jugarlo, y un control A/B demuestra que la memoria y la pantalla
  quedan idénticas byte a byte.
- **Repetible hasta el byte.** El reloj del juego es el contador de instrucciones. Con la misma
  cinta de entrada y el mismo corte, dos corridas dejan cero bytes de diferencia, así que si algo
  cambia, lo has cambiado tú.
- **Pensado para la IA, sin fiarse de la IA.** Cada función es una carpeta autónoma con hechos
  medidos y un único fichero que escribir. El modelo recibe hechos en vez de conjeturas, y el
  juez decide si su respuesta está bien. Las mismas carpetas sirven a una persona sin ninguna IA.
- **Arte HD por identidad.** Cada sprite se identifica en el momento en que el juego lo dibuja, no
  buscando píxeles en la pantalla después. El kit redacta el encargo de cada asset, rechaza las
  entregas que romperían el juego y cambia el arte mientras se juega, sin tocar la memoria del
  juego.
- **Una plataforma comprobada contra una referencia.** Pequeños programas DOS sintéticos comparan
  PakuPort con DOSBox-X subsistema a subsistema, y cada comprobación del proyecto se ha visto
  fallar a propósito antes de fiarse de ella.
- **Un `.exe` de doble clic al final.** El port es un programa .NET normal; `port` puede además
  empaquetarlo como un ejecutable autónomo para jugar.

Medido en uno de los juegos de prueba del autor, un arcade comercial de principios de los 80: se
ha rehecho entero en C# con el tablero y el juez, 61 piezas. Una partida grabada de 398 millones
de instrucciones (título, menú, todas las fases, fin de partida y récords) corre sin que el
intérprete ejecute ni una sola instrucción del original, y la memoria y la pantalla salen
idénticas byte a byte a las del original.

## Qué trae

| | |
|---|---|
| **Port** | `pakuport port JUEGO.EXE` → un proyecto .NET en `export/<id>/` que juega al juego, y si quieres un `.exe` de doble clic |
| **Plataforma** | DOS (ficheros, memoria, EXEC, TSR), BIOS, modos VGA/EGA/CGA/texto, Mode X, VESA, PIT/PIC/DMA, teclado, ratón, joystick, altavoz, Sound Blaster (incluido DMA de 16 bits), OPL2, MPU-401; hosts DPMI/VCPI para DOS/4GW, DOS/16M y extensores embebidos; paginación |
| **Medir** | decenas de interruptores `PAKU_*` que registran quién dibuja qué, quién suena, qué código corrió, cada llamada a DOS, cada entrada; una cinta de entrada determinista |
| **El tablero del remake** | `pakuport expediente tablero`: una carpeta por función con lo medido, su desensamblado y un esqueleto en C# |
| **El juez** | `pakuport banco juzga`: reproduce llamadas grabadas contra tu C#; compara registros, banderas, memoria y hasta el número de instrucciones (el "ritmo") |
| **Preguntar al original** | `pakuport oracle-run`: ejecuta una función original con la entrada que tú elijas |
| **Enchufarlo** | `pakuport export --portadas`: tus traducciones corren dentro del juego; un control A/B demuestra que no cambia nada |
| **Kit de arte HD** | `pakuport remake`: captura cada sprite con su identidad, redacta el encargo de arte, valida lo que entregas y lo sustituye en tiempo real |
| **Sondas de plataforma** | pequeños programas DOS sintéticos en `tools/` que comparan PakuPort con DOSBox-X, subsistema a subsistema |

## Empezar

**¿Sólo quieres ver un juego porteado?** Descarga
[`JugarCatch-win-x64.zip`](https://github.com/unmateria/PakuPort/releases/latest/download/JugarCatch-win-x64.zip), descomprímelo y
haz doble clic en `JugarCatch.exe`: el juego de ejemplo, porteado por PakuPort, sin instalar nada.

**Para portear y rehacer juegos** necesitas Windows, el [SDK de .NET 10](https://dotnet.microsoft.com/download)
y Python 3.10+. Descarga [`PakuPort-win-x64.zip`](https://github.com/unmateria/PakuPort/releases/latest/download/PakuPort-win-x64.zip)
(el repositorio con la línea de comandos ya compilada en `bin\`), o clónalo y compílalo:

```powershell
git clone https://github.com/unmateria/PakuPort pakuport
cd pakuport
dotnet build PakuPort.slnx -c Release
python tools\selftest.py
```

`selftest.py` recorre la cadena entera sobre el juego de ejemplo y tiene que acabar en
`SELFTEST: GREEN`. Después portea tú el ejemplo. `pakuport.cmd`, en la raíz, lanza la línea de comandos (la ya
compilada de `bin\` si está, y si no `dotnet run` sobre `src\`):

```powershell
.\pakuport port samples\catch\dos\CATCH.COM --no-package
cd export\catch
dotnet run -c Release
```

El [tutorial](docs/TUTORIAL.md) sigue desde ahí, paso a paso, hasta tener funciones traducidas
corriendo dentro del juego.

## Cómo se usa, en una página

1. **Portear**: `pakuport port JUEGO.EXE`. Apunta a una carpeta que contenga solo el juego:
   `port` copia la carpeta entera como datos del juego.
2. **Fijar la velocidad**: añade `"cyclesPerMs": 3000` (o la que vaya bien con Ctrl+F1/F2) a
   `games/<id>/game.json` y vuelve a exportar (`pakuport export --game ...`). Las cintas, el A/B y
   el kit de arte lo necesitan, porque en PakuPort el reloj del juego **es** el contador de
   instrucciones.
3. **Grabar lo que hace**, en una sola corrida: `PAKU_CORPUS` (las llamadas reales de cada
   función), `PAKU_CAPAS` (quién dibuja, suena o lee la entrada) y `PAKU_COBERTURA` (qué código
   corrió de verdad).
4. **Comprobar la grabación**: `pakuport corpus-diag` — cada caso tiene que reproducirse.
5. **Generar el tablero**: `pakuport expediente tablero` — una carpeta por función.
6. **Traducir** una función a `respuesta.cs` y **juzgarla**: `pakuport banco juzga --exige-ritmo`.
7. **Ver el juez en rojo** una vez, con una mutación a propósito: un juez que nunca se ha visto
   fallar no prueba nada.
8. **Enchufarla al juego** (`pakuport export --portadas`) y demostrar con
   `tools/control_portadas.py` que la memoria y la pantalla salen idénticas byte a byte.

**Con IA**: dale a un modelo una carpeta del tablero cada vez, con `COMO-ESCRIBIR.md`; que
escriba solo `respuesta.cs` y que el juez decida. La guía, con un prompt que funciona y los
fallos que hay que avisarle, está en [docs/REMAKE-WITH-AI.md](docs/REMAKE-WITH-AI.md).
**Sin IA**: el mismo flujo a mano, más cómo preguntarle cosas nuevas al binario original, en
[docs/REMAKE-BY-HAND.md](docs/REMAKE-BY-HAND.md).

## Documentación

La documentación detallada está en inglés:

| Lee | para |
|---|---|
| [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) | instalar, compilar, verificar, portear el primer programa, las teclas de un juego porteado |
| [docs/TUTORIAL.md](docs/TUTORIAL.md) | el recorrido completo sobre el juego de ejemplo |
| [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) | la arquitectura y el *porqué* de cada decisión |
| [docs/REMAKE-WITH-AI.md](docs/REMAKE-WITH-AI.md) | cómo darle el tablero a un modelo de lenguaje sin fiarse de él |
| [docs/REMAKE-BY-HAND.md](docs/REMAKE-BY-HAND.md) | el mismo trabajo sin ninguna IA |
| [docs/HD-ART.md](docs/HD-ART.md) | el kit de arte: capturar, encargar, entregar, validar, juzgar |
| [docs/CLI.md](docs/CLI.md) | todos los comandos |
| [docs/PROBES.md](docs/PROBES.md) | las sondas de plataforma y el oráculo DOSBox-X |
| [docs/LIMITATIONS.md](docs/LIMITATIONS.md) | lo que no funciona, dicho claro |
| [CONTRIBUTING.md](CONTRIBUTING.md) | las reglas de la casa |

## Por qué está hecho así

PakuPort empezó como recompilador estático y se dio contra el muro de siempre: en software de
DOS, **saber qué bytes son código es indecidible** (tablas de saltos, código automodificable,
overlays, protecciones que se descifran a sí mismas). Por eso hoy el motor es un **intérprete**:
ejecuta lo que ejecutaría la CPU, y como el juego corre bajo su control, puede **observarlo**.
Lo que escribe no es una conjetura sobre lo que el código podría hacer, sino el registro de lo
que hizo.

También se probó a pedirle a modelos de lenguaje que reescribieran un juego en C# limpio a partir
del desensamblado, y fallaron como fallan siempre: inventan comportamiento y omiten parte de él
sin decirlo. De ahí la regla central: **al modelo se le dan hechos medidos y cada respuesta la
comprueba un juez**. Y de ahí las otras: ningún juez vale hasta que se le ha visto en rojo; una
comparación sin nada que comparar es un fallo, no un aprobado; y los fallos se arreglan en la
plataforma, por especificación, nunca con un parche para un juego concreto.

## Estado

PakuPort se desarrolló contra unas dos docenas de juegos comerciales de DOS, desde programas
`.COM` de modo real de los 80 hasta títulos de 32 bits con DOS/4GW. La mayoría se juegan de
principio a fin. **Este repositorio no incluye ningún juego ni nombra ninguno**: trae tus propias
copias legales.

Es una herramienta de nivel investigación: potente, medida y con opiniones firmes, pero sin
pulir. Parte de los comentarios del código y de los mensajes de la línea de comandos están en
español. Lee [docs/LIMITATIONS.md](docs/LIMITATIONS.md) antes de empezar.

## Licencia

GPL-3.0 o posterior. Partes de la capa de plataforma derivan de DOSBox-X (GPL-2.0 o posterior) y
están marcadas en el código con el fichero y la línea de origen; el escalador xBRZ es GPL-3.0.
Ver [NOTICE.md](NOTICE.md) y [LICENSES/THIRD-PARTY.txt](LICENSES/THIRD-PARTY.txt).

PakuPort no incluye juegos con derechos de autor ni debe usarse para distribuirlos. Los ports
que produce contienen el código y los datos del juego original y son para uso propio con copias
que poseas.
