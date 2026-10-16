# Palancas de diagnostico `PAKU_*` — indice GENERADO

> **No editar a mano.** Lo genera `tools/gen_palancas.py` leyendo `src/`.
> Regenerar: `python tools/gen_palancas.py`
> Comprobar que no se ha quedado desfasado: `python tools/gen_palancas.py --check`
> (sale distinto de cero si el codigo cambio y esto no; sirve como gate).

**145 palancas** en **36 ficheros**.

**12 sin documentar** — no tienen ningun comentario que las explique:

`PAKU_CORPUS_BUCLE_MIN`, `PAKU_CORPUS_MAXFX`, `PAKU_CORPUS_MAXINSN`, `PAKU_CORPUS_MAXPRE`, `PAKU_CORPUS_MAXW`, `PAKU_DUMP_DECOMP`, `PAKU_DUMP_SRC`, `PAKU_DYNUNPACK_BUDGET`, `PAKU_EMIT_TRACE`, `PAKU_EXIT_STACK`, `PAKU_INT21_KEY_TRACE`, `PAKU_IPLOG_DUMP`

Todas estan APAGADAS por defecto y ninguna altera el comportamiento del juego.

## Indice alfabetico

| Palanca | Forma | Ambito | Donde vive |
|---|---|---|---|
| `PAKU_API_EXTENSOR` | presencia o valor | motor | Core/Platform/FlatServices.cs |
| `PAKU_AUTOEXIT_INSN` | numero | motor | Core/CorteDeterminista.cs |
| `PAKU_AUTOEXIT_MS` | numero / presencia o valor | motor + plantilla | Cli/Program.cs |
| `PAKU_AUTOKEYS` | valor de texto | plantilla | Cli/Program.cs |
| `PAKU_CAPAS` | valor de texto | motor | Core/PerfilCapas.cs |
| `PAKU_CB_ANIDADO` | presencia o valor | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_CB_FLAGS` | presencia o valor | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_CB_IRET` | =1 | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_CLICKS` | valor de texto | plantilla | Cli/Program.cs |
| `PAKU_COBERTURA` | valor de texto | motor | Core/Cobertura.cs |
| `PAKU_CORPUS` | valor de texto | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_BUCLE_MIN` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_FUNCS` | valor de texto | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAX` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAXFUN` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAXFX` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAXINSN` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAXPRE` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAXW` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_MAX_NOV` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_NOVEDADES` | valor de texto | motor | Core/CorpusRecorder.cs |
| `PAKU_CORPUS_VENTANA_NOV` | numero | motor | Core/CorpusRecorder.cs |
| `PAKU_CORTE_EN_INT` | =1 | motor | Core/CorteDeterminista.cs |
| `PAKU_CORTE_FOTO` | =1 | motor | Core/CorteDeterminista.cs |
| `PAKU_CRUCE_CB` | =1 | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_CYCLES` | numero | plantilla | Cli/Program.cs |
| `PAKU_DIAG_CB` | =1 | motor | Core/Platform/DosPlatform.cs |
| `PAKU_DISCO_<LETRA>` | sin determinar | motor | Core/Platform/DosFileSystem.cs |
| `PAKU_DPMI_RAW` | =0 | motor | Core/Platform/FlatServices.cs |
| `PAKU_DS_WATCH` | =1 | motor | Core/Cpu.cs |
| `PAKU_DUMP32` | valor de texto | motor | Core/Interp.cs |
| `PAKU_DUMP_DECOMP` | =1 | motor | Core/ExePackDecompressor.cs |
| `PAKU_DUMP_SRC` | presencia o valor | motor | Cli/Program.cs |
| `PAKU_DYNUNPACK` | valor de texto | motor | Cli/Program.cs |
| `PAKU_DYNUNPACK_BUDGET` | numero | motor | Cli/Program.cs |
| `PAKU_EMIT_TRACE` | =1 | motor | Cli/Program.cs |
| `PAKU_ESCALA_ENTERA` | =1 | plantilla | Cli/Program.cs |
| `PAKU_EXIT_STACK` | =1 | motor | Cli/Program.cs |
| `PAKU_FATAL_MEMDUMP` | =1 | motor | Core/Interp.cs |
| `PAKU_FB_PNG` | valor de texto | plantilla | Cli/Program.cs |
| `PAKU_FILE_TRACE` | =1 | motor | Core/Platform/Int21hHandler.cs |
| `PAKU_FOTOGRAMAS` | valor de texto | motor | Core/GrabadorFotogramas.cs |
| `PAKU_FOTOGRAMA_EN` | valor de texto | motor | Core/GrabadorFotogramas.cs |
| `PAKU_GAP_TRACE` | =1 | motor | Core/Cpu.cs |
| `PAKU_HEADLESS` | =1 | motor | Core/CorteDeterminista.cs |
| `PAKU_HORA_FIJA` | =1 | motor | Core/Platform/FlatServices.cs |
| `PAKU_INPUT_LOG` | valor de texto | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_INPUT_REPLAY` | valor de texto | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_INT16_PARK` | =1 | motor | Core/Platform/Int16hHandler.cs |
| `PAKU_INT16_TRACE` | =1 | motor | Core/Platform/Int16hHandler.cs |
| `PAKU_INT21_KEY_TRACE` | =1 | motor | Core/Platform/Int21hHandler.cs |
| `PAKU_INT33_COUNT` | =1 | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_INTERP` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_INTERP32_CACHE` | presencia o valor | motor | Core/Interp32.cs |
| `PAKU_INTERP_CACHE` | presencia o valor | motor | Core/Interp.cs |
| `PAKU_INTERP_ONLY` | =1 | plantilla | Cli/Program.cs |
| `PAKU_INTERP_TRACE` | =1 | motor | Core/Interp.cs |
| `PAKU_INTERP_TRACE_FROM` | numero | motor | Core/Interp.cs |
| `PAKU_INTERP_TRACE_TO` | numero | motor | Core/Interp.cs |
| `PAKU_IPLOG_DUMP` | presencia o valor | motor | Cli/Program.cs |
| `PAKU_IRQ_DET` | numero | motor | Core/Platform/FlatServices.cs |
| `PAKU_IRQ_MAX` | numero | motor | Core/Platform/FlatServices.cs |
| `PAKU_LECTURA_COMPLETA` | =1 | motor | Core/Platform/Int21hHandler.cs |
| `PAKU_LIFTED` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_LIVE_VRAM` | =1 | plantilla | Cli/Program.cs |
| `PAKU_LOG_AUDIO_EV` | =1 | motor | Core/AudioEventLog.cs |
| `PAKU_LOG_BLIT` | numero | motor | Core/BlitProvenance.cs |
| `PAKU_LOG_CRTC` | =1 / =2 | motor | Core/Platform/VideoState.cs |
| `PAKU_LOG_DMA` | =1 | motor | Core/Platform/Machine/Dma8237.cs |
| `PAKU_LOG_DPMI` | =1 | motor | Core/Platform/FlatServices.cs |
| `PAKU_LOG_FAR32` | =1 | motor | Core/Interp32.cs |
| `PAKU_LOG_FUERA32` | =1 | motor | Core/Interp.cs |
| `PAKU_LOG_INT21` | =1 | motor | Core/Platform/Int21hHandler.cs |
| `PAKU_LOG_INT33` | =1 | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_LOG_INTJUEGO` | valor de texto | motor | Core/Interp.cs |
| `PAKU_LOG_IRQ32` | =1 | motor | Core/Platform/FlatServices.cs |
| `PAKU_LOG_IVT` | =1 | motor | Core/Platform/Machine/Ivt.cs |
| `PAKU_LOG_KBD` | =1 | motor | Core/Platform/DosPlatform.cs |
| `PAKU_LOG_MIDI` | =1 | motor | Core/Platform/Machine/Mpu401.cs |
| `PAKU_LOG_OPL` | =1 | motor | Core/Platform/Machine/Opl.cs |
| `PAKU_LOG_PAGINAS` | =1 | motor | Core/Memory.cs |
| `PAKU_LOG_PALBIOS` | =1 | motor | Core/Platform/Int10hHandler.cs |
| `PAKU_LOG_PALETTE` | =1 | motor | Core/Platform/VideoState.cs |
| `PAKU_LOG_PORTS32` | =1 | motor | Core/Interp32.cs |
| `PAKU_LOG_PRESENT` | =1 | motor | Core/Platform/VideoState.cs |
| `PAKU_LOG_SB` | =1 | motor | Core/Platform/Machine/HostAudio.cs |
| `PAKU_LOG_SPK` | =1 | motor | Core/Platform/Machine/Speaker.cs |
| `PAKU_LOG_VBE` | =1 | motor | Core/Platform/Int10hHandler.cs |
| `PAKU_MEMDUMP` | valor de texto | motor + plantilla | Cli/Program.cs |
| `PAKU_MEMDUMP_ALTO` | valor de texto | plantilla | Cli/Program.cs |
| `PAKU_MEMWATCH` | numero | motor | Core/Memory.cs |
| `PAKU_MEMWATCH_ALL` | =1 | motor | Core/Memory.cs |
| `PAKU_MICKEY_1X` | =1 | motor | Core/Platform/Int33hHandler.cs |
| `PAKU_MOUSE_ABS` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_MOUSE_WIGGLE` | valor de texto | plantilla | Cli/Program.cs |
| `PAKU_NOAUDIO` | =1 | motor + plantilla | Cli/Program.cs |
| `PAKU_NOSPLASH` | =1 | plantilla | Cli/Program.cs |
| `PAKU_NO_ASPECT` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_NO_DPMI` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_NO_GAMEPORT` | =1 / presencia o valor | motor + plantilla | Cli/Program.cs |
| `PAKU_NO_MOUSE_CAPTURE` | =1 | plantilla | Cli/Program.cs |
| `PAKU_NO_TYPEMATIC` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_NREDIR` | =1 | motor | Core/Cpu.cs |
| `PAKU_OPL_GAIN` | numero | motor | Core/Platform/Machine/Opl.cs |
| `PAKU_PLATFORM_TRACE` | =1 | motor | Core/Platform/PlatformDebug.cs |
| `PAKU_PRESENT_CAPTURE` | =1 | motor | Core/Platform/VideoState.cs |
| `PAKU_PRESENT_DUMP` | presencia o valor / valor de texto | motor | Core/Platform/VideoState.cs |
| `PAKU_PRESENT_DUMP_AFTER_FRAMES` | numero | motor | Core/Platform/VideoState.cs |
| `PAKU_PRESENT_DUMP_AFTER_MS` | numero | motor | Core/Platform/VideoState.cs |
| `PAKU_PRESENT_DUMP_EVERY` | numero | motor | Core/Platform/VideoState.cs |
| `PAKU_PRESENT_DUMP_N` | numero | motor | Core/Platform/VideoState.cs |
| `PAKU_PROC_PIXEL` | =1 | motor | Core/Platform/VideoState.cs |
| `PAKU_PROC_PIXEL_EN` | numero | motor | Core/Platform/VideoState.cs |
| `PAKU_RASTRO` | valor de texto | motor | Core/RastroDeterminismo.cs |
| `PAKU_RASTRO_MEM` | valor de texto | motor | Core/RastroDeterminismo.cs |
| `PAKU_RELOJ_PARED` | valor de texto | motor | Core/InputTape.cs |
| `PAKU_REMAKE` | presencia o valor / valor de texto | motor | Core/BlitProvenance.cs |
| `PAKU_REMAKE_GRACE_MS` | numero | motor | Core/RemakeMap.cs |
| `PAKU_REMAKE_NO_AGUJEREAR` | =1 | plantilla | Cli/Program.cs |
| `PAKU_SAMPLE_PC` | =1 | motor | Core/Interp.cs |
| `PAKU_SAMPLE_PC32` | =1 | motor | Core/Interp.cs |
| `PAKU_SAMPLE_PC_MS` | numero | motor | Core/Interp.cs |
| `PAKU_SB_COLCHON_MS` | numero | motor | Core/Platform/Machine/SoundBlaster.cs |
| `PAKU_SB_RELOJ_INVITADO` | =1 | motor | Core/Platform/FlatServices.cs |
| `PAKU_SB_SIN_ABORTO` | =1 | motor | Core/Platform/Machine/SoundBlaster.cs |
| `PAKU_SCALE3X` | =1 | motor | Core/EscaladoPixelArt.cs |
| `PAKU_SCREENSHOT_CADA` | numero | plantilla | Cli/Program.cs |
| `PAKU_SIN_ATAJO_DOS` | =1 | motor | Core/Interp.cs |
| `PAKU_SIN_FRENO` | =1 | motor | Core/Platform/Machine/MachineTimer.cs |
| `PAKU_SIN_PAGINACION` | =1 | motor | Core/Memory.cs |
| `PAKU_SIN_PARADA_VUELTA` | =1 | motor | Cli/BancoCiego.cs |
| `PAKU_SIN_PORTADAS` | =1 | motor | Core/FuncionesPortadas.cs |
| `PAKU_TAPE_RECORD` | valor de texto | motor | Core/InputTape.cs |
| `PAKU_TAPE_REPLAY` | valor de texto | motor | Core/InputTape.cs |
| `PAKU_TRACE` | =1 | motor | Core/Platform/DosPlatform.cs |
| `PAKU_TRACE_EX` | =1 | plantilla | Cli/Program.cs |
| `PAKU_TRAZA_EIP_VRAM` | =1 | motor | Core/Platform/VideoState.cs |
| `PAKU_TRAZA_FICHEROS` | =1 | motor | Core/Platform/FlatServices.cs |
| `PAKU_TRAZA_LECTURA` | valor de texto | motor | Core/Memory.cs |
| `PAKU_TRAZA_VAR` | valor de texto | motor | Core/Memory.cs |
| `PAKU_WATCH_FS` | =1 | motor | Core/Interp32.cs |
| `PAKU_WATCH_LIN` | numero | motor | Core/Memory.cs |
| `PAKU_WAV_DUMP` | valor de texto | plantilla | Cli/Program.cs |
| `PAKU_WINDOWED` | presencia o valor | plantilla | Cli/Program.cs |
| `PAKU_XBRZ` | numero | plantilla | Cli/Program.cs |


## Motor — valen en el motor y sus herramientas (`pakuport ...`, `le-run`)

### `src/PakuPort.Cli/BancoCiego.cs`

#### `PAKU_SIN_PARADA_VUELTA`  ·  =1

EL CONTROL NEGATIVO de la parada, y por eso es una palanca y no un experimento suelto: con `PAKU_SIN_PARADA_VUELTA=1` los casos de vuelta se reproducen SIN parar en la cabecera. Tienen que salir ROJOS — el replay sigue dando vueltas y compara el estado de otra iteración. Un mecanismo que nadie ha visto fallar no prueba nada.

### `src/PakuPort.Cli/Program.cs`

#### `PAKU_AUTOEXIT_MS`  ·  numero / presencia o valor

Verificación automatizada (CI/diagnóstico, genérico): PAKU_NOSPLASH=1     salta el splash (la lógica arranca ya) PAKU_AUTOEXIT_MS=N  cierra tras N ms reportando estado + screenshot.png

Usada en: `src/PakuPort.Cli/Program.cs:1107`, `src/PakuPort.Cli/Program.cs:2836`, `src/PakuPort.Core/CorteDeterminista.cs:35`

#### `PAKU_DUMP_SRC`  ·  presencia o valor

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_DYNUNPACK`  ·  valor de texto

TAREA A — Unpack dinámico como PASO ADICIONAL. Su salida SUSTITUYE al memimage/realEntry SOLO cuando el corte se produce (transfer far descendente). El camino EXEPACK estático (EXEPACK/VROOMM) NO se toca: por defecto ("auto") el dinámico solo se intenta cuando el estático no detecta EXEPACK; PAKU_DYNUNPACK=force lo fuerza siempre (para el oráculo interno), =off lo desactiva.

#### `PAKU_DYNUNPACK_BUDGET`  ·  numero

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_EMIT_TRACE`  ·  =1

**SIN DOCUMENTAR** — no hay comentario que la explique.

Usada en: `src/PakuPort.Cli/Program.cs:5511`, `src/PakuPort.Cli/Program.cs:5744`

#### `PAKU_EXIT_STACK`  ·  =1

**SIN DOCUMENTAR** — no hay comentario que la explique.

Usada en: `src/PakuPort.Cli/Program.cs:5777`, `src/PakuPort.Cli/Program.cs:5797`, `src/PakuPort.Core/Cpu.cs:843`

#### `PAKU_IPLOG_DUMP`  ·  presencia o valor

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_MEMDUMP`  ·  valor de texto

PAKU_MEMDUMP=<fichero>: vuelca el MiB de memoria VIVA al terminar. Es la única forma de leer el código que un juego genera o descomprime en runtime (el de la imagen del EXE ya no es el que corre) y de ver dónde ha dejado sus vectores: así se localizó el driver de teclado de un juego de prueba, colgado de INT 15h y no del INT 9.

Usada en: `src/PakuPort.Cli/Program.cs:1470`, `src/PakuPort.Cli/Program.cs:3710`, `src/PakuPort.Core/CorteDeterminista.cs:77`

#### `PAKU_NOAUDIO`  ·  =1

SIN dispositivo de audio (PAKU_NOAUDIO) se escribe SOLO lo que el anillo tenia, sin rellenar con silencio hasta completar el bufer. La rama de arriba SI rellena, y debe: alli hay un dispositivo esperando un bloque entero cada vez. Aqui no hay nadie a quien alimentar, y rellenar convertia el WAV en un reloj de FOTOGRAMAS en vez de un reloj de AUDIO: la duracion del volcado salia proporcional a los fps del juego. Medido — un juego a ~59 fps daba 247 s de WAV para 45 s de partida (2665 bufers de 2048 = 122.880 muestras/s) mientras otro, a ~11 fps, daba 45,0 s clavados y parecia que el que estaba roto era el primero. El 84,9 % de silencio de aquel volcado era este relleno, no el juego.

Usada en: `src/PakuPort.Cli/Program.cs:1052`, `src/PakuPort.Cli/Program.cs:2731`, `src/PakuPort.Core/Platform/Machine/Mpu401.cs:172`

#### `PAKU_NO_GAMEPORT`  ·  =1 / presencia o valor

del usuario sin joystick físico). PAKU_NO_GAMEPORT=0 fuerza el puerto presente para A/B.

Usada en: `src/PakuPort.Cli/Program.cs:2627`, `src/PakuPort.Core/Platform/Machine/GamePort.cs:44`

### `src/PakuPort.Core/AudioEventLog.cs`

#### `PAKU_LOG_AUDIO_EV`  ·  =1

Remake-kit step 5: sound-event catalog with fingerprints, so the offline tooling can name the sounds a game makes and a future remake manifest can map each fingerprint to an HD sample. Two sources, one log (logs/audio-events.log), enabled by PAKU_LOG_AUDIO_EV=1: spk — one event per gate-on -> gate-off sequence of PIT channel 2, carrying the list of (frequency Hz, duration ms) segments that actually sounded. A frequency change while the gate stays open opens a new segment (that is how DOS jingles are played). opl — one event per burst of OPL register writes separated by silences (>150 ms with no key-on active): the register program that produced the sound. The fingerprint is FNV-1a over the QUANTIZED sequence (Hz rounded to integer, ms rounded to 4 ms buckets for spk; the raw reg/val byte stream for opl) so the same sound effect played twice hashes the same despite the jitter of the real-time clock. The "who" is the caller's cs:ip (Interp.CurrentPc), free telemetry that ties each sound to the routine that triggered it — the same provenance idea as blit.log. Off (default) = one static bool check on the audio tick and on each OPL register write; nothing else in the emulator changes. This is telemetry only: it never touches CPU/memory state nor the generated samples. Line format (one per event): t=<tick> src=spk fnv=XXXXXXXX n=<items> pc=CCCC:IIII ms=<total> fnvhz=XXXXXXXX seq=hz:ms,... t=<tick> src=opl fnv=XXXXXXXX n=<items> pc=CCCC:IIII ms=<total> regs=RR:VV,... The seq/regs detail is capped at  items with a "+N" tail; the fingerprint always covers the WHOLE sequence.

### `src/PakuPort.Core/BlitProvenance.cs`

#### `PAKU_LOG_BLIT`  ·  numero

Off by default. Also on under PAKU_REMAKE: the HD replacement layer needs the chain to identify an asset composed in RAM, not just the file->VRAM shortcut.

Usada en: `src/PakuPort.Core/BlitProvenance.cs:12`, `src/PakuPort.Core/BlitRawLog.cs:46`, `src/PakuPort.Core/Interp.cs:88`

#### `PAKU_REMAKE`  ·  presencia o valor / valor de texto

Off by default. Also on under PAKU_REMAKE: the HD replacement layer needs the chain to identify an asset composed in RAM, not just the file->VRAM shortcut.

Usada en: `src/PakuPort.Core/BlitProvenance.cs:13`, `src/PakuPort.Core/RemakeMap.cs:88`

### `src/PakuPort.Core/Cobertura.cs`

#### `PAKU_COBERTURA`  ·  valor de texto

PAKU_COBERTURA=<fichero>: marca el rango de cada instruccion que el interprete ejecuta y al terminar vuelca los TRAMOS de codigo pisados. Es la frontera de lo desconocido del expediente: lo que no aparece aqui no se ejecuto nunca, y sobre eso la IA no tiene datos. Solo mide el mundo de 16 bits; lo de fuera se cuenta y se declara.

### `src/PakuPort.Core/CorpusRecorder.cs`

#### `PAKU_CORPUS`  ·  valor de texto

Enciende el grabador si PAKU_CORPUS está puesto. Devuelve el aviso a imprimir, o null si no se pidió nada. Se llama una vez al arrancar el hilo del juego.

#### `PAKU_CORPUS_BUCLE_MIN`  ·  numero

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_CORPUS_FUNCS`  ·  valor de texto

Con lista se graban esas y sólo esas. Sin lista, modo AUTO: las funciones se aprenden de los `call` que la propia ejecución hace, que es lo que permite grabar juegos donde `discover` no llega — un COM, por ejemplo, que no es un MZ y ni siquiera carga.

#### `PAKU_CORPUS_MAX`  ·  numero

GRABADOR DE CORPUS POR FUNCIÓN — el banco de pruebas que acompaña a cada función del port. Tesis (día 1 del plan del expediente): una función que llega acompañada de N invocaciones reales grabadas se puede reescribir a ciegas y saber en un segundo si está bien. Es lo que convierte "la IA reescribe y reza" en un bucle cerrado con test. Enganche por SP, en la cabecera del bucle del intérprete. No toca ninguna rama de : en cada instrucción ya están CS, IP y SP, y eso basta. <list type="bullet"> <item>si (cs,ip) es el arranque de una función de interés → se abre un caso con los registros de entrada, el DGROUP y el SP de ese instante;</item> <item>si un caso abierto ve que SP ha subido por encima del suyo → la función ha retornado, y se cierra con el estado de ese momento.</item> </list> Así vale igual para call near, far e indirecto, y hasta para una función a la que se llega por un jmp de cola. Lo único que se marca en las ramas de call es el DESTINO, y sólo para el modo auto (ver ). Honestidad del cierre. Al abrir se anota la dirección de retorno que hay en la pila; al cerrar se exige que el IP coincida con ella. Si no coincide, el caso se descarta y se cuenta como cierre sucio: es una función que no retorna por donde se la llamó (el FarRet-unwind de un juego con co-rutinas, un longjmp, una co-rutina). Grabar esos casos sería sembrar el corpus de pares entrada/salida que no se corresponden — un verde falso esperando a pasar. Alcance de la memoria grabada: el DGROUP de 64 KB desde el DS de entrada, que es exactamente lo que el juez de casos (PakuPort.Judge, DiffEngine) sabe comparar. Límite conocido, y escrito en el propio índice: lo que la función toque fuera de esa ventana (VRAM, un buffer en otro segmento) no entra en el caso. Por qué el formato se escribe aquí a mano y no con CorpusCase: el contenedor PFC1 vive en el juez de casos (PakuPort.Judge), que NO viaja a los exports. Un export es un producto self-contained. Así que aquí se escribe el contenedor documentado, byte a byte, y el juez lo lee con su propio CorpusCase: si los dos lados no coinciden, la verificación sale roja — que es justo la prueba que se quiere. Palancas: PAKU_CORPUS=<dir> lo enciende; PAKU_CORPUS_FUNCS=<fichero> da la lista de funciones (el functions.json de pakuport discover, o un .txt de offsets hex) y sin ella va en modo auto; PAKU_CORPUS_MAX los casos por función (8) y PAKU_CORPUS_MAXFUN el tope de funciones en auto (400).

#### `PAKU_CORPUS_MAXFUN`  ·  numero

GRABADOR DE CORPUS POR FUNCIÓN — el banco de pruebas que acompaña a cada función del port. Tesis (día 1 del plan del expediente): una función que llega acompañada de N invocaciones reales grabadas se puede reescribir a ciegas y saber en un segundo si está bien. Es lo que convierte "la IA reescribe y reza" en un bucle cerrado con test. Enganche por SP, en la cabecera del bucle del intérprete. No toca ninguna rama de : en cada instrucción ya están CS, IP y SP, y eso basta. <list type="bullet"> <item>si (cs,ip) es el arranque de una función de interés → se abre un caso con los registros de entrada, el DGROUP y el SP de ese instante;</item> <item>si un caso abierto ve que SP ha subido por encima del suyo → la función ha retornado, y se cierra con el estado de ese momento.</item> </list> Así vale igual para call near, far e indirecto, y hasta para una función a la que se llega por un jmp de cola. Lo único que se marca en las ramas de call es el DESTINO, y sólo para el modo auto (ver ). Honestidad del cierre. Al abrir se anota la dirección de retorno que hay en la pila; al cerrar se exige que el IP coincida con ella. Si no coincide, el caso se descarta y se cuenta como cierre sucio: es una función que no retorna por donde se la llamó (el FarRet-unwind de un juego con co-rutinas, un longjmp, una co-rutina). Grabar esos casos sería sembrar el corpus de pares entrada/salida que no se corresponden — un verde falso esperando a pasar. Alcance de la memoria grabada: el DGROUP de 64 KB desde el DS de entrada, que es exactamente lo que el juez de casos (PakuPort.Judge, DiffEngine) sabe comparar. Límite conocido, y escrito en el propio índice: lo que la función toque fuera de esa ventana (VRAM, un buffer en otro segmento) no entra en el caso. Por qué el formato se escribe aquí a mano y no con CorpusCase: el contenedor PFC1 vive en el juez de casos (PakuPort.Judge), que NO viaja a los exports. Un export es un producto self-contained. Así que aquí se escribe el contenedor documentado, byte a byte, y el juez lo lee con su propio CorpusCase: si los dos lados no coinciden, la verificación sale roja — que es justo la prueba que se quiere. Palancas: PAKU_CORPUS=<dir> lo enciende; PAKU_CORPUS_FUNCS=<fichero> da la lista de funciones (el functions.json de pakuport discover, o un .txt de offsets hex) y sin ella va en modo auto; PAKU_CORPUS_MAX los casos por función (8) y PAKU_CORPUS_MAXFUN el tope de funciones en auto (400).

#### `PAKU_CORPUS_MAXFX`  ·  numero

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_CORPUS_MAXINSN`  ·  numero

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_CORPUS_MAXPRE`  ·  numero

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_CORPUS_MAXW`  ·  numero

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_CORPUS_MAX_NOV`  ·  numero

EL CORPUS GUIADO POR COBERTURA (`PAKU_CORPUS_NOVEDADES=<fichero>`). El hueco que tapa. La cuota guarda las OCHO PRIMERAS invocaciones de cada unidad, y un juego hace casi siempre lo mismo: en un juego de prueba los ocho primeros turnos del jugador son el arranque, y el disparo que acierta, el fin de la partida o el record no entran nunca. Traducir esos caminos sin caso es traducir sin red. Como. Dos pasadas con la MISMA cinta, que el arnes ya es determinista. La primera (PAKU_COBERTURA) apunta en <csv>.novedades el instante en que cada direccion de codigo se ejecuto por primera vez. La segunda lee esos instantes y, con la cuota ya llena, abre tambien las invocaciones que empiezan poco antes de uno (PAKU_CORPUS_VENTANA_NOV instrucciones, 400.000 por defecto) y solo escribe las que de verdad lo contienen. Cada instante acaba cubierto por un caso de CADA unidad que lo envuelve, de la mas interior a la de fuera. Tope por unidad: PAKU_CORPUS_MAX_NOV (64).

#### `PAKU_CORPUS_NOVEDADES`  ·  valor de texto

EL CORPUS GUIADO POR COBERTURA (`PAKU_CORPUS_NOVEDADES=<fichero>`). El hueco que tapa. La cuota guarda las OCHO PRIMERAS invocaciones de cada unidad, y un juego hace casi siempre lo mismo: en un juego de prueba los ocho primeros turnos del jugador son el arranque, y el disparo que acierta, el fin de la partida o el record no entran nunca. Traducir esos caminos sin caso es traducir sin red. Como. Dos pasadas con la MISMA cinta, que el arnes ya es determinista. La primera (PAKU_COBERTURA) apunta en <csv>.novedades el instante en que cada direccion de codigo se ejecuto por primera vez. La segunda lee esos instantes y, con la cuota ya llena, abre tambien las invocaciones que empiezan poco antes de uno (PAKU_CORPUS_VENTANA_NOV instrucciones, 400.000 por defecto) y solo escribe las que de verdad lo contienen. Cada instante acaba cubierto por un caso de CADA unidad que lo envuelve, de la mas interior a la de fuera. Tope por unidad: PAKU_CORPUS_MAX_NOV (64).

#### `PAKU_CORPUS_VENTANA_NOV`  ·  numero

EL CORPUS GUIADO POR COBERTURA (`PAKU_CORPUS_NOVEDADES=<fichero>`). El hueco que tapa. La cuota guarda las OCHO PRIMERAS invocaciones de cada unidad, y un juego hace casi siempre lo mismo: en un juego de prueba los ocho primeros turnos del jugador son el arranque, y el disparo que acierta, el fin de la partida o el record no entran nunca. Traducir esos caminos sin caso es traducir sin red. Como. Dos pasadas con la MISMA cinta, que el arnes ya es determinista. La primera (PAKU_COBERTURA) apunta en <csv>.novedades el instante en que cada direccion de codigo se ejecuto por primera vez. La segunda lee esos instantes y, con la cuota ya llena, abre tambien las invocaciones que empiezan poco antes de uno (PAKU_CORPUS_VENTANA_NOV instrucciones, 400.000 por defecto) y solo escribe las que de verdad lo contienen. Cada instante acaba cubierto por un caso de CADA unidad que lo envuelve, de la mas interior a la de fuera. Tope por unidad: PAKU_CORPUS_MAX_NOV (64).

### `src/PakuPort.Core/CorteDeterminista.cs`

#### `PAKU_AUTOEXIT_INSN`  ·  numero

`PAKU_CORTE_FOTO=1` with `PAKU_AUTOEXIT_INSN`: instead of exiting at the cut, freeze the guest and let the host save the COMPOSED screen (game + HD layers) as screenshot.png. `PAKU_FB_PNG` gives the guest's own frame; this is what the player would see, at a repeatable instant, which is what a before/after comparison of HD art needs.

#### `PAKU_CORTE_EN_INT`  ·  =1

`PAKU_CORTE_EN_INT=1`: pasado el limite no se corta en el poll, sino en la PRIMERA interrupcion software que ejecute el programa despues. Para el A/B de un juego TRADUCIDO. Una traduccion cobra sus instrucciones por bloques (ver Original.Instrucciones), asi que en mitad de un bloque su memoria no tiene por que coincidir con la del original en esa misma instruccion: una ya escribio lo que la otra aun no. Justo antes de un `int` las dos tienen que estar igual — la traduccion cobra hasta el `int` incluido y hace antes todo lo que el original habia hecho. Cortar ahi compara lo comparable, y el corte sigue siendo el mismo en cualquier corrida.

#### `PAKU_CORTE_FOTO`  ·  =1

`PAKU_CORTE_FOTO=1` with `PAKU_AUTOEXIT_INSN`: instead of exiting at the cut, freeze the guest and let the host save the COMPOSED screen (game + HD layers) as screenshot.png. `PAKU_FB_PNG` gives the guest's own frame; this is what the player would see, at a repeatable instant, which is what a before/after comparison of HD art needs.

#### `PAKU_HEADLESS`  ·  =1

UNA CORRIDA SIN VENTANA: no se abre ventana visible, no se roba el foco y no se captura el raton. Es UNA sola definicion a proposito — estaba escrita cinco veces en la plantilla del host, siempre como "hay PAKU_AUTOEXIT_MS", y al anadir el corte por instrucciones esos cinco sitios se quedaron fuera: las corridas de medida empezaron a abrir ventana y a robarle el foco al usuario, que es justo lo que la regla de la casa prohibe. `PAKU_HEADLESS=1` la fuerza sin ninguna condicion de parada.

### `src/PakuPort.Core/Cpu.cs`

#### `PAKU_DS_WATCH`  ·  =1

PAKU_DS_WATCH=1: avisa de cada cambio de DS. Para cazar el juego que pierde su segmento de datos, que se ve como lecturas de basura muy lejos de la causa.

#### `PAKU_GAP_TRACE`  ·  =1

Telemetry: FarRet unwinds redirected to their real destination (co-routine) and those skipped by the intra-module fail-safe. Redirects ~0 outside co-routine blitters in a healthy game. Logged individually under PAKU_GAP_TRACE.

#### `PAKU_NREDIR`  ·  =1

Near-guard co-routine redirect master switch (5º intento). OFF by default after a manual-playtest regression: in a real game the redirect recursed Interp.Run per lifted↔interp bounce (BUILD-click killed the host with no exit.log = stack overflow) and ran continuations hardware wouldn't at that point (wagon lists vanished). PAKU_NREDIR=1 re-enables it for experiments; the NCORA gate runs with it ON to keep the mechanism proven while the shipped default stays safe. The proper fix needs the oracle side-by-side (menus front).

### `src/PakuPort.Core/EscaladoPixelArt.cs`

#### `PAKU_SCALE3X`  ·  =1

PAKU_SCALE3X=1: amplía el frame del juego con Scale3x antes de presentarlo.

### `src/PakuPort.Core/ExePackDecompressor.cs`

#### `PAKU_DUMP_DECOMP`  ·  =1

**SIN DOCUMENTAR** — no hay comentario que la explique.

Usada en: `src/PakuPort.Core/ExePackDecompressor.cs:201`, `src/PakuPort.Core/ExePackDecompressor.cs:332`

### `src/PakuPort.Core/FuncionesPortadas.cs`

#### `PAKU_SIN_PORTADAS`  ·  =1

`PAKU_SIN_PORTADAS=1` apaga TODAS las traducciones sin recompilar. Es lo que permite el control A/B honesto: la misma cinta, el MISMO ejecutable, y como unica diferencia si el gancho esta o no. Comparar dos binarios distintos no probaria nada.

### `src/PakuPort.Core/GrabadorFotogramas.cs`

#### `PAKU_FOTOGRAMAS`  ·  valor de texto

`PAKU_FOTOGRAMAS=<fichero.jsonl>` + `PAKU_FOTOGRAMA_EN=<ip hex>[,...]` — the original game, frame by frame, as ground truth for a hand-written (idiomatic) remake. Why it exists. The per-case judge compares registers, stack and instruction count, so the only C# it accepts is a line-by-line copy of the assembly. A readable remake has none of those; what it shares with the original is the game STATE at each frame and what is on screen. This records exactly that: every time execution reaches one of the given addresses (the top of the game's frame loop), one JSON line with the data segment and the CGA screen as they are at that instant, plus every hardware input the game read since the previous line (port reads and INT 16h answers) and every port write (the sound). A remake fed with those inputs must reach the same state, frame after frame. Memory and screen are stored as changed runs against the previous line (the first line is against all zeros), so a long session stays small. The events are kept from the start of the program, so the first line carries everything that happened before it (a title, a menu). Cost when off: one static bool read per instruction, like its siblings in Interp.

#### `PAKU_FOTOGRAMA_EN`  ·  valor de texto

`PAKU_FOTOGRAMAS=<fichero.jsonl>` + `PAKU_FOTOGRAMA_EN=<ip hex>[,...]` — the original game, frame by frame, as ground truth for a hand-written (idiomatic) remake. Why it exists. The per-case judge compares registers, stack and instruction count, so the only C# it accepts is a line-by-line copy of the assembly. A readable remake has none of those; what it shares with the original is the game STATE at each frame and what is on screen. This records exactly that: every time execution reaches one of the given addresses (the top of the game's frame loop), one JSON line with the data segment and the CGA screen as they are at that instant, plus every hardware input the game read since the previous line (port reads and INT 16h answers) and every port write (the sound). A remake fed with those inputs must reach the same state, frame after frame. Memory and screen are stored as changed runs against the previous line (the first line is against all zeros), so a long session stays small. The events are kept from the start of the program, so the first line carries everything that happened before it (a title, a menu). Cost when off: one static bool read per instruction, like its siblings in Interp.

### `src/PakuPort.Core/InputTape.cs`

#### `PAKU_RELOJ_PARED`  ·  valor de texto

`PAKU_RELOJ_PARED=<que>` — devuelve al RELOJ DE PARED lo que la cinta pasó al del invitado, para poder medir A/B si algo se rompe con la cinta puesta y no sin ella. Valores, separados por comas: retrace (la fase de 0x3DA), sb (la IRQ del bloque de la Sound Blaster), hora (fecha y hora, ver GuestClock), espera (el tiempo que pasa mientras la BIOS espera una tecla, ver Int16hHandler.EsperaUnPoco), todo. Sin la variable no cambia nada. Existe porque un cambio que solo se activa con la cinta no se puede bisecar de otra forma: el sintoma aparece justo en la corrida que uno quiere medir.

#### `PAKU_TAPE_RECORD`  ·  valor de texto

Remake-kit step 3: deterministic input record/replay. (Env vars are PAKU_TAPE_*, not PAKU_INPUT_*: PAKU_INPUT_REPLAY already belongs to the older mouse-only replay in Int33hHandler and must keep working.) PAKU_TAPE_RECORD=<file>: every input primitive that enters the emulation (INT16h EnqueueKey, 8042 EnqueueHostScancode, INT33h SetPosition/SetButtons) is logged with its Cpu.TickCount stamp — the sinks record, so synthetic sources (autokeys, wiggle, script clicks) are captured for free. PAKU_TAPE_REPLAY=<file>: the host template suppresses live input and re-injects the tape at the same ticks. Both modes lock MachineTimer to the virtual clock (TickCount/CyclesPerMs) so IRQ0/BDA/ VGA land on the same instruction-stream points every run; both REQUIRE a calibrated governor (CyclesPerMs > 0). Off (neither var set) = zero cost, nothing changes. Tape line format: "<tick> <E|H|M|T> args" (E sc ascii · H scancode · M x y · T buttons).

#### `PAKU_TAPE_REPLAY`  ·  valor de texto

Remake-kit step 3: deterministic input record/replay. (Env vars are PAKU_TAPE_*, not PAKU_INPUT_*: PAKU_INPUT_REPLAY already belongs to the older mouse-only replay in Int33hHandler and must keep working.) PAKU_TAPE_RECORD=<file>: every input primitive that enters the emulation (INT16h EnqueueKey, 8042 EnqueueHostScancode, INT33h SetPosition/SetButtons) is logged with its Cpu.TickCount stamp — the sinks record, so synthetic sources (autokeys, wiggle, script clicks) are captured for free. PAKU_TAPE_REPLAY=<file>: the host template suppresses live input and re-injects the tape at the same ticks. Both modes lock MachineTimer to the virtual clock (TickCount/CyclesPerMs) so IRQ0/BDA/ VGA land on the same instruction-stream points every run; both REQUIRE a calibrated governor (CyclesPerMs > 0). Off (neither var set) = zero cost, nothing changes. Tape line format: "<tick> <E|H|M|T> args" (E sc ascii · H scancode · M x y · T buttons).

### `src/PakuPort.Core/Interp.cs`

#### `PAKU_DUMP32`  ·  valor de texto

PAKU_DUMP32=<fichero>: vuelca la imagen del mundo plano tal como quedó. Es la única forma de comparar byte a byte lo que cargamos con lo que hay en el disco: un programa que comprueba su propia integridad dice que algo no cuadra, nunca DÓNDE.

#### `PAKU_FATAL_MEMDUMP`  ·  =1

Con PAKU_FATAL_MEMDUMP=1 además se vuelca la imagen de memoria: un cuelgue no lanza excepción, así que este es el único momento en que se puede desensamblar el código VIVO del bucle (los segmentos de overlay se reutilizan).

#### `PAKU_INTERP_CACHE`  ·  presencia o valor

── CACHE DE DECODIFICACION DEL MUNDO DE 16 BITS ───────────────────────────────────────── Hermano del de Interp32 (ver alli el porque y la invalidacion por generacion de pagina), con UNA diferencia que lo hace seguro: solo cachea codigo por encima del megabyte. La memoria baja la escriben mil caminos del mundo real que no pasan por WrLin8 y vigilarlos todos costaria mas que el cache; ademas, en modo real la lineal NUNCA llega ahi, asi que todos los juegos probados corren exactamente como antes. No es un caso raro: en un juego DOS/4GW Pro el 99,7 % de las instrucciones de 16 bits se buscan por encima del megabyte (218 M de 218,9 M medidas en 30 s), porque el kernel del extensor vive en selectores con base alta. Sin cache, cada byte del opcode se lee con RdLin8 y paga su traduccion de pagina: 3-5 traducciones por instruccion solo para leerla.

#### `PAKU_INTERP_TRACE`  ·  =1

Directorio de overlays VROOMM (ovno → file key del inicio del código), para resolver thunks INT 3F sin parchear — espejo del lowering estático del Lifter. Lo fija el host exportado desde el game.json.

#### `PAKU_INTERP_TRACE_FROM`  ·  numero

PAKU_INTERP_TRACE_FROM: first guest-clock tick (TickCount) the trace logs, so studying one key press does not mean logging the whole boot.

#### `PAKU_INTERP_TRACE_TO`  ·  numero

PAKU_INTERP_TRACE_TO: last guest-clock tick the trace logs.

#### `PAKU_LOG_FUERA32`  ·  =1

PAKU_LOG_FUERA32=1 (la misma palanca que en Interp32): cada escritura que se sale del espacio, con la instrucción, los selectores y sus descriptores crudos.

Usada en: `src/PakuPort.Core/Interp.cs:57`, `src/PakuPort.Core/Interp32.cs:123`

#### `PAKU_LOG_INTJUEGO`  ·  valor de texto

`PAKU_LOG_INTJUEGO=<n>[,<n>...]` (numeros en HEX) — QUIEN LLAMA AL DRIVER DEL JUEGO, con que registros y desde donde. Para que existe. En DOS, un juego habla con su driver por una interrupcion propia: un juego de prueba tiene su driver grafico en INT 91h con el numero de funcion en AX, otro tiene el p-code de Turbo Basic en INT ECh. Cuando ese driver rechaza una llamada, el mensaje dice el numero malo y la direccion de retorno, y con eso hay que encontrar al llamante — que no se puede buscar en el volcado de memoria: medido en un juego de prueba, en el MiB entero hay cuatro cd 91 y ninguno cae donde tendria que caer, porque la llamada la hace un envoltorio de libreria o un puntero calculado. Anota una linea por vectorizacion a logs/intjuego.log: el tick, el numero, de donde viene (CS:IP de la instruccion y direccion de retorno, que es la que imprimen los drivers), a donde va, los registros, el tope de la pila y el camino recorrido. Esas dos cosas no son un extra: un driver DOS recibe sus parametros por la pila (el envoltorio de C hace mov ax,[bp+6]), y el camino es lo que resolvio el caso del INT 91h, porque enseno que el programa no venia de la instruccion anterior sino atravesando los ceros del principio de otro segmento. Apagado no cuesta nada: una comparacion contra un array de 256 bools que se llena una vez.

#### `PAKU_SAMPLE_PC`  ·  =1

PAKU_SAMPLE_PC=1: on process exit, dump the black-box ring (last 64 interpreted cs:ip) to stderr — localizes a busy loop that never faults (hangs have no exception to dump on).

#### `PAKU_SAMPLE_PC32`  ·  =1

PAKU_SAMPLE_PC32=1: histograma del EIP del mundo plano. Es LA herramienta para un juego que corre y no avanza: dice en qué bucle está metido, que es lo único que un log de servicios no puede contar. El tamaño del trozo se hace IRREGULAR mientras muestrea para que el corte no caiga siempre en el mismo punto de un bucle de periodo divisor.

#### `PAKU_SAMPLE_PC_MS`  ·  numero

PAKU_SAMPLE_PC_MS=<guest ms>: PERIODIC PC sampling to logs/pcsample.log, the raw input of the offline scene segmenter (tools/scene_hist; requirement 10 of REMAKE-IA-REQUISITOS.md: "escenas detectadas por histograma de PC"). Fully independent of PAKU_SAMPLE_PC above, which stays exactly as it was (one-shot ring dump for hangs).

#### `PAKU_SIN_ATAJO_DOS`  ·  =1

PAKU_SIN_ATAJO_DOS=1: devuelve el `int 21h AH=2Ch` del mundo mixto al kernel del extensor, que es como estaba antes. Es el CONTROL del A/B — ver el atajo en .

### `src/PakuPort.Core/Interp32.cs`

#### `PAKU_INTERP32_CACHE`  ·  presencia o valor

── CACHE DE DECODIFICACIÓN ──────────────────────────────────────────────────────────────── Sin él, cada instrucción EJECUTADA se decodifica con Iced otra vez — y decodificar cuesta más que ejecutar una instrucción simple. El techo medido era ~11,5M instr/s, que en un juego 3D de 32 bits son ~7 fps de motor a CUALQUIER velocidad de governor (y sin cadencia no hay paralaje). Es el "acelerador" que el pivote a intérprete-puro dejó pendiente. La INVALIDACIÓN es lo delicado: un juego de prueba se AUTOMODIFICA (parchea inmediatos del motor 3D en runtime). Se resuelve con una GENERACIÓN POR PÁGINA de 4K: toda escritura al espacio alto incrementa la generación de su página (Memory.WrLin8Directo), y una entrada del cache sólo vale si la generación combinada de sus páginas (la de la instrucción y la siguiente, por si cruza) no ha cambiado. El índice de página va con máscara: el aliasing sólo puede invalidar DE MÁS, nunca de menos. El cache sólo sirve código del espacio ALTO (>1 MiB): el bajo lo escriben mil caminos del mundo de 16 bits que no pasan por WrLin8, y vigilarlos todos costaría más que el cache. La clave es (lineal, eip): la MISMA dirección lineal decodificada con otro EIP (un selector con base) daría destinos de salto distintos. PAKU_INTERP32_CACHE=0 lo apaga (el A/B de la escotilla).

#### `PAKU_LOG_FAR32`  ·  =1

PAKU_LOG_FAR32=1: traza cada transferencia far con su selector, su offset y la dirección lineal resultante. Una llamada far que acaba en memoria vacía es indepurable sin esto: el destino sale de un puntero en memoria y no se ve en el desensamblado.

#### `PAKU_LOG_PORTS32`  ·  =1

PAKU_LOG_PORTS32=1: cada in/out del mundo plano. Un driver de sonido que "no encuentra hardware" sondea puertos, y sin ver esa conversación no hay forma de saber si el que no contesta somos nosotros o es que sondea otra dirección.

#### `PAKU_WATCH_FS`  ·  =1

PAKU_WATCH_FS=1: avisa de cada cambio de FS con la instruccion que lo hizo. El driver de sonido de HMI toma su segmento de datos DE FS (`push fs` / `pop ds`), asi que quien lo ponga a cero le deja los punteros de funcion apuntando a la IVT.

### `src/PakuPort.Core/Memory.cs`

#### `PAKU_LOG_PAGINAS`  ·  =1

PAKU_LOG_PAGINAS=1: las primeras traducciones, diciendo cuáles son identidad y cuáles mueven la página de sitio.

#### `PAKU_MEMWATCH`  ·  numero

PAKU_MEMWATCH=<hex lineal>: depuración — imprime el stack trace C# (cadena de Fn lifteadas) la primera vez que se escribe esa dirección con OTRO valor. Robusto ante var vacía/basura ('' con Convert.ToInt32 tiraba TypeInitializationException y mataba la clase Memory entera ).

#### `PAKU_MEMWATCH_ALL`  ·  =1

Sin la condicion "y el valor cambia": para saber si a una direccion NO la escribe NADIE hace falta ver tambien las escrituras del mismo valor. PAKU_MEMWATCH_ALL=1.

#### `PAKU_SIN_PAGINACION`  ·  =1

Enciende o apaga la paginación con la CR3 vigente. Vaciar el TLB al cambiar cualquiera de las dos es obligatorio: las traducciones cacheadas son de otro mundo. PAKU_SIN_PAGINACION=1: no traducir aunque el programa encienda PG (lineal = física, como antes de que esto existiera). Es la escotilla del A/B — la que separa "el juego falla por la traducción" de "falla por otra cosa".

#### `PAKU_TRAZA_LECTURA`  ·  valor de texto

PAKU_TRAZA_LECTURA=<hexIni>-<hexFin>: QUÉ direcciones de esa región lee el juego, en un buffer circular con las últimas. El vigilante de al lado mira ESCRITURAS, y hay preguntas que sólo contestan las lecturas: qué parte de un mapa está mirando el jugador ahora mismo. En un motor 3D que dibuja desde un mapa, las direcciones que su renderizador lee del mapa dibujan el abanico de visión, y el vértice de ese abanico es la cámara. Preguntárselo al juego en vez de buscar sus variables a ciegas. Apagada (sin la variable) no cuesta nada: una comparación contra un rango vacío.

#### `PAKU_TRAZA_VAR`  ·  valor de texto

PAKU_TRAZA_VAR=<hex>[,<hex>...]: la TRAYECTORIA de unas cuantas variables del mundo plano, no sólo quién las toca. El vigilante de abajo identifica al culpable y se para a los 12 avisos, y además avisa por BYTE, así que de un dword sólo enseña una esquina: sirve para saber quién escribe, no para ver cómo evoluciona un valor. Se apunta una muestra con el valor de TODAS las variables cada vez que se completa una escritura sobre la PRIMERA de la lista (la disparadora). El disparo va en su último byte y no en el primero: `WrLin32` escribe de byte en byte, así que mirando el primero se leería el dword a medias — mitad valor nuevo, mitad viejo. Para qué: grabando la posición y la orientación de una cámara se reconstruye su trayectoria fotograma a fotograma, que es lo que hace falta para redibujar una escena desde donde el juego la está mirando.

#### `PAKU_WATCH_LIN`  ·  numero

PAKU_WATCH_LIN=<hex lineal>: quién escribe en esa dirección del espacio PLANO, con el EIP del intérprete de 32 bits. El PAKU_MEMWATCH de arriba no sirve aquí: aquel vigila el mundo de 16 bits por seg:off y no ve nada de lo que pasa por encima del megabyte.

### `src/PakuPort.Core/PerfilCapas.cs`

#### `PAKU_CAPAS`  ·  valor de texto

PAKU_CAPAS=<fichero.csv>: perfila QUIEN hace cada cosa durante una partida real — quien escribe en la VRAM, quien toca cada puerto y quien llama a cada INT—, apuntado al CS:IP que lo ejecuta. Es lo que permite decir, con una medida y no con un parecido de nombre, que funcion es render, cual es audio, cual es E/S y cual es LOGICA, que es lo unico que un remake necesita reescribir. Lo agrega `pakuport expediente capas`.

### `src/PakuPort.Core/Platform/DosFileSystem.cs`

#### `PAKU_DISCO_<LETRA>`  ·  sin determinar

PALANCA: PAKU_DISCO_<LETRA>=<ruta del host> (letters D..Z) mounts a host folder as that DOS drive. C: is always the game directory. Example: PAKU_DISCO_D=C:\ruta\modulos. The generated host also fills these variables from a DISCOS.txt file next to the exe (one D=C:\ruta per line), so the player never has to touch environment variables.

### `src/PakuPort.Core/Platform/DosPlatform.cs`

#### `PAKU_DIAG_CB`  ·  =1

Sirve las IRQs hardware pendientes (timer, teclado) que el PIC tenga listas y no enmascaradas, mientras IF esté activo. Se llama desde el HILO DEL JUEGO en un punto seguro (límite de frame de Cpu.Step), de modo que disparar la ISR (que reentra a FarDispatch sobre el mismo cpu/mem) no compita con el hilo del host. Guarda de reentrada por si una ISR vuelve a cruzar un límite de frame. DIAG (heartbeat [HB] del host): contadores de despacho de IRQs, permanentes.

#### `PAKU_LOG_KBD`  ·  =1

PAKU_LOG_KBD=1: traza del bus del 8042 (encolado, transferencia al latch con su IRQ1 y lectura de 0x60 con el PC del lector). Es la única forma de ver por qué se atasca la cola.

Usada en: `src/PakuPort.Core/Platform/DosPlatform.cs:279`, `src/PakuPort.Core/Platform/Machine/KeyboardController.cs:23`

#### `PAKU_TRACE`  ·  =1

Trazado de diagnóstico, off por defecto (PAKU_TRACE=1). Sin coste en runs normales.

Usada en: `src/PakuPort.Core/Platform/DosPlatform.cs:277`, `src/PakuPort.Core/Platform/Int21hHandler.cs:123`

### `src/PakuPort.Core/Platform/FlatServices.cs`

#### `PAKU_API_EXTENSOR`  ·  presencia o valor

OPT-IN por juego (`game.json "apiExtensor": true`). Un extensor EMBEBIDO no habla DPMI con su cliente: le ofrece su propia API por INT 21h, y aunque los números parezcan los de DOS no lo son. Va con interruptor porque COLISIONAN: un juego DOS/4GW corre sobre DOS/4GW y usa el AH=25 de toda la vida por este mismo camino.

#### `PAKU_DPMI_RAW`  ·  =0

SE OFRECE. Antes no, con el argumento de que el contrato de registros de esas rutinas no se podia inventar — cierto, pero ya no hay que inventarlo: esta MEDIDO en el propio cliente (un juego de prueba, 08A2:14D7 y 08A2:48A3) y es el camino por el que el extensor entrega el control a su juego. Por el otro, el de 0300, se rinde y sale con AH=4C. La escotilla queda al reves para el A/B: PAKU_DPMI_RAW=0 lo retira. Solo lo ven los juegos con `dpmiHost`.

#### `PAKU_HORA_FIJA`  ·  =1

La hora que ve el juego. Con el reloj determinista está CONGELADA: un juego de prueba pide `AH=2C` al arrancar y siembra con ella su generador aleatorio, así que la hora de pared hacía que dos ejecuciones del mismo binario tomaran caminos distintos — medido, era la segunda fuente de no determinismo después del temporizador. PAKU_HORA_FIJA=1: congela la hora SIN tocar la fuente del temporizador. Hace falta para medir con reloj de pared un juego cuya pantalla de protección se siembra con la hora (medido en un juego de prueba): sin esto, la única forma de precalcular la respuesta y automatizar el paso era PAKU_IRQ_DET, que además cambia el reloj y por tanto lo que se quería medir.

#### `PAKU_IRQ_DET`  ·  numero

PAKU_IRQ_DET=N: el temporizador deja de ir por el reloj de PARED y pasa a ir por el contador de instrucciones del invitado — un tick de IRQ0 cada N instrucciones. No es una opcion de rendimiento, es una herramienta de diagnostico, y la unica que hace bisecable un fallo intermitente: con el reloj de pared la IRQ cae en una instruccion distinta en cada ejecucion, asi que dos trazas del MISMO binario no se pueden comparar y el reventon aparece en un sitio distinto cada vez (medido en un juego DOS/4GW: 19/40/20 IRQ y tres direcciones sin relacion). Con este reloj, dos ejecuciones son la misma ejecucion. N son las instrucciones por MILISEGUNDO virtual, no por tick: el periodo del tick sale de la frecuencia que el juego le haya programado al PIT. Tiene que ser asi para que el reloj del temporizador y el del audio —que cuenta en milisegundos, `Cpu.CyclesPerMs`— vayan acompasados; con un numero fijo de instrucciones por tick, un juego que baja su PIT a 18,2 Hz veia pasar el tiempo 3 veces mas despacio de lo que le sonaba la tarjeta. Valor razonable: el `cyclesPerMs` habitual de los juegos probados, 3000.

#### `PAKU_IRQ_MAX`  ·  numero

PAKU_IRQ_MAX=K: entrega sólo las K primeras IRQ y a partir de ahí ninguna (se les hace el ACK igual). Es la bisección: con el reloj determinista, si con K no revienta y con K+1 sí, la IRQ culpable es la K+1 y se sabe la instrucción exacta en la que se entregó.

#### `PAKU_LOG_DPMI`  ·  =1

PAKU_LOG_DPMI=1: cada servicio DPMI con lo que entra y lo que sale. Un cliente que se rinde tras una llamada fallida no dice cuál era: sin esto no hay forma de saberlo.

#### `PAKU_LOG_IRQ32`  ·  =1

PAKU_LOG_IRQ32=1: cada IRQ entregada con su vector, dónde se interrumpió y con qué pila. Sin esto, "revienta en la IRQ 17" no dice en qué estaba el juego cuando le entró.

#### `PAKU_SB_RELOJ_INVITADO`  ·  =1

PAKU_SB_RELOJ_INVITADO=1: devuelve el DMA de la tarjeta al reloj del invitado, que es como estaba antes. Es el CONTROL del A/B de los microcortes: sin poder medir el comportamiento viejo en las mismas condiciones, "ahora suena bien" es una impresión.

#### `PAKU_TRAZA_FICHEROS`  ·  =1

PAKU_TRAZA_FICHEROS=1: cada seek y cada lectura con su posición real. Sin esto, entender por qué un juego elige mal un offset dentro de un catálogo es imposible.

### `src/PakuPort.Core/Platform/Int10hHandler.cs`

#### `PAKU_LOG_PALBIOS`  ·  =1

PAKU_LOG_PALBIOS=1: traza cada servicio de paleta del BIOS (INT 10h AH=10h) con sus argumentos, y para las cargas por bloque (AL=02/12) el contenido que se carga. Un juego que pinta imágenes con tabla de color propia programa la paleta desde aquí, así que sin esta traza no se distingue "el juego pidió estos colores" de "son los que dejó el set-mode y nadie los tocó" — y esa diferencia es justo la que decide si un color medido en pantalla viene del juego o de nuestro propio valor por defecto.

#### `PAKU_LOG_VBE`  ·  =1

PAKU_LOG_VBE=1: traza cada llamada VBE (función, registros, buffer).

### `src/PakuPort.Core/Platform/Int16hHandler.cs`

#### `PAKU_INT16_PARK`  ·  =1

PAKU_INT16_TRACE=1: cada servicio de teclado del BIOS con lo que devuelve. Es la forma de saber si un juego NO lee por aqui (muchos instalan su propia ISR del 09h).

#### `PAKU_INT16_TRACE`  ·  =1

PAKU_INT16_TRACE=1: cada servicio de teclado del BIOS con lo que devuelve. Es la forma de saber si un juego NO lee por aqui (muchos instalan su propia ISR del 09h).

### `src/PakuPort.Core/Platform/Int21hHandler.cs`

#### `PAKU_FILE_TRACE`  ·  =1

DEBUG: ring buffer of last file ops (PAKU_FILE_TRACE=1)

#### `PAKU_INT21_KEY_TRACE`  ·  =1

**SIN DOCUMENTAR** — no hay comentario que la explique.

#### `PAKU_LECTURA_COMPLETA`  ·  =1

DIAGNOSTICO (PAKU_LECTURA_COMPLETA=1): contesta que se leyo todo. NO es fidelidad — DOS devuelve lo que hay— pero responde una pregunta que no se puede responder de otra forma: si a un programa que aborta por una lectura corta le importan de verdad esos bytes, o si el fichero que tenemos esta simplemente recortado al final.

#### `PAKU_LOG_INT21`  ·  =1

Every [int21]/[4A] line carries t=<Cpu.TickCount>, the machine-wide time axis shared by blit.log / blitraw.bin / audio-events.log / input.log / the input tape — without it the DOS trace cannot be crossed with the rest of the telemetry. The stamp is appended at the END of each line (and before the trailing "(CS=...)" of the AH=3D lines) so the existing field parsers (tools/dossier/build_dossier.py) keep working.

### `src/PakuPort.Core/Platform/Int33hHandler.cs`

#### `PAKU_CB_ANIDADO`  ·  presencia o valor

PAKU_CB_ANIDADO=1 vuelve a la conducta VIEJA: llamar al trampolin del extensor de forma anidada, con un far-call y esperando un RETF. Esta medida y NO da raton —el trampolin se paraba a 34 instrucciones, o acababa ejecutando datos—, pero se conserva como escape. El camino normal es el SALTO con la cadena de retorno del original, que es el que entrega el raton de verdad (ver ).

#### `PAKU_CB_FLAGS`  ·  presencia o valor

FULL flags word, not just IF: the real callback runs inside the driver's hardware ISR, whose IRET restores the interrupted code's FLAGS verbatim. This dispatch fires at an arbitrary instruction boundary — restoring only IF let the game's callback leak CF/ZF/... into a cmp/jcc straddle (one game's INT 91h dispatcher interrupted between cmp and jae → bogus "invalid function 4" exit). PAKU_CB_FLAGS=0 reverts (A/B).

#### `PAKU_CB_IRET`  ·  =1

PAKU_CB_IRET=1: llamar al trampolin del extensor con marco de INTERRUPCION (tres words) e IsrMode, en vez del far call de dos words de siempre. Es la pieza que falta para que el trampolin termine su trabajo —su codigo acaba en `iret`— pero TAL CUAL rompe: con ella el trampolin avanza de verdad (ya no se para a 34 instrucciones) y el juego muere en 0F34:0B78. Ojo al desensamblado: en 204B hay un `iret` y en 204C un `retf`, o sea que el trampolin tiene DOS salidas y probablemente dos contratos de entrada; falta ver contra el oraculo cual usa el driver de raton de DOSBox y con que pila.

#### `PAKU_CRUCE_CB`  ·  =1

PAKU_CRUCE_CB=1: intenta CRUZAR a modo real para llamar al trampolin de un extensor (ver ServiceCallback). Apagado por defecto porque esta A MEDIO CAMINO: con el cruce el trampolin de un juego DOS/4GW Pro llega hasta su `ljmp 0070:4F11` y se descarrila dentro del kernel (3E58:0104), porque le estamos fabricando un contexto de modo real que no es el suyo. Se conserva encendible para seguir por ahi con la medida hecha.

#### `PAKU_INPUT_LOG`  ·  valor de texto

PAKU_INPUT_LOG=<fichero>: apunta cada muestra APLICADA con su Cpu.TickCount. PAKU_INPUT_REPLAY=<fichero>: en Poll se aplican las muestras del log cuando TickCount alcanza el registrado (el host deja de encolar de Raylib de facto: _hostEvents se ignora) ⇒ corridas de ratón reproducibles (red del ScriptDriver headless y para bisecar crashes).

#### `PAKU_INPUT_REPLAY`  ·  valor de texto

PAKU_INPUT_LOG=<fichero>: apunta cada muestra APLICADA con su Cpu.TickCount. PAKU_INPUT_REPLAY=<fichero>: en Poll se aplican las muestras del log cuando TickCount alcanza el registrado (el host deja de encolar de Raylib de facto: _hostEvents se ignora) ⇒ corridas de ratón reproducibles (red del ScriptDriver headless y para bisecar crashes).

#### `PAKU_INT33_COUNT`  ·  =1

Diagnostics: enabled with PAKU_INT33_COUNT=1. Proves the game polls the mouse at runtime (before the mouse snapshot fix it never called INT 33h).

#### `PAKU_LOG_INT33`  ·  =1

PAKU_LOG_INT33=1: traza de llamadas al driver a stderr (AH=03 muestreada 1/256 para no inundar)

#### `PAKU_MICKEY_1X`  ·  =1

PAKU_LOG_INT33=1: traza de llamadas al driver a stderr (AH=03 muestreada 1/256 para no inundar)

### `src/PakuPort.Core/Platform/Machine/Dma8237.cs`

#### `PAKU_LOG_DMA`  ·  =1

PAKU_LOG_DMA=1: cada escritura a un registro de los dos controladores DMA, con su puerto y su valor. Es la telemetria que separa "el juego no programa el DMA" de "lo programa donde no miramos": el driver de un juego de prueba escribia en 0x8B —la pagina del canal 5, el de 16 bits— y esa escritura acababa en el canal 3 porque el indice se enmascaraba con &3. Sin ver los puertos crudos, el sintoma era un contador que no se movia nunca.

### `src/PakuPort.Core/Platform/Machine/HostAudio.cs`

#### `PAKU_LOG_SB`  ·  =1

PAKU_LOG_SB=1: resumen por segundo del SUBDESBORDE del anillo (ver Read). Va con la misma palanca que el resto del audio porque se leen juntos: el del SB dice si el invitado alimenta el DMA, y éste dice si lo que se alimentó llega a sonar.

Usada en: `src/PakuPort.Core/Platform/Machine/HostAudio.cs:41`, `src/PakuPort.Core/Platform/Machine/SoundBlaster.cs:32`

### `src/PakuPort.Core/Platform/Machine/Ivt.cs`

#### `PAKU_LOG_IVT`  ·  =1

PAKU_LOG_IVT=1: trace every vector write (who hooks/unhooks what, and from where).

### `src/PakuPort.Core/Platform/Machine/MachineTimer.cs`

#### `PAKU_SIN_FRENO`  ·  =1

`PAKU_SIN_FRENO=1` — el governor no duerme, SOLO con una cinta puesta. Con cinta, el reloj del invitado es el contador de instrucciones (ticks del PIT, la cinta misma, el retrace y la IRQ del Sound Blaster: ver ), asi que frenar a cyclesPerMs solo cuesta tiempo de pared y no cambia nada de lo que el juego ve. Una corrida de medida de 40 M de instrucciones de un juego de prueba pasa de 2 minutos a lo que tarde el interprete. Sin cinta se ignora: ahi el freno ES el ritmo del juego.

### `src/PakuPort.Core/Platform/Machine/Mpu401.cs`

#### `PAKU_LOG_MIDI`  ·  =1

PAKU_LOG_MIDI=1: la conversacion con el MPU-401 (entrada en modo UART, reset, y los comandos del intelligent mode que se reconocen sin modelar). Responde la pregunta de si un juego ha encontrado la tarjeta MIDI o se ha caido al OPL, que desde fuera suenan distinto pero no dicen por que.

### `src/PakuPort.Core/Platform/Machine/Opl.cs`

#### `PAKU_LOG_OPL`  ·  =1

PAKU_LOG_OPL=1: cada escritura al sintetizador FM. CERO escrituras con el juego sonando significa que su musica no va por AdLib — asi se descarto en un juego de prueba.

#### `PAKU_OPL_GAIN`  ·  numero

Ganancia aplicada al int32 de GenerateBlock2 antes de clampar a int16 (§4.3). 1.0 = fiel a DOSBox sin adlib_pcm_boost. Calibrable por env (PAKU_OPL_GAIN) contra el RMS del oráculo.

### `src/PakuPort.Core/Platform/Machine/SoundBlaster.cs`

#### `PAKU_SB_COLCHON_MS`  ·  numero

Colchón que se mantiene por delante del cursor de reproducción. Ha de ser holgado frente al bloque de drenaje del render y quedar MUY por debajo de la capacidad del anillo, que comparte con el OPL y el speaker. TODO LO QUE HAY EN ESTE COLCHON ES LATENCIA: son muestras ya decididas, esperando turno, así que el sonido de un evento se oye `colchon` segundos después de que el juego lo lance. Estaba en 0,5 s = 5,4 bloques de drenaje, y una prueba manual lo oyó: "una explosión suena como 500 ms después de un choque". El bloque del host son 2048 muestras a 22.050 Hz = 92,9 ms (Cli/Program.cs, SetAudioStreamBufferSizeDefault), y el render lo drena a 60 fps, así que con DOS bloques de margen sobra para absorber un frame largo sin quedarse en seco. PAKU_SB_COLCHON_MS reabre el valor: es el CONTROL del A/B (con el número viejo tienen que volver la latencia y el `anillo=` de [AUDIO]) y la salida si algún equipo lento mete huecos.

#### `PAKU_SB_SIN_ABORTO`  ·  =1

PAKU_SB_SIN_ABORTO=1: una transferencia nueva NO aborta lo que quedara pendiente de la anterior, que es como estaba antes. Es el CONTROL del A/B: sin poder medir la conducta vieja en las mismas condiciones, "ahora no se pierde sonido" es una impresion. Y es tambien la escotilla si algun juego resultara depender de la cola.

### `src/PakuPort.Core/Platform/Machine/Speaker.cs`

#### `PAKU_LOG_SPK`  ·  =1

PAKU_LOG_SPK=1: el altavoz interno — puerta del canal 2 y flancos del bit 1 del 0x61 (audio bit-bangeado). Con el modo directo, los flancos son la senal.

### `src/PakuPort.Core/Platform/PlatformDebug.cs`

#### `PAKU_PLATFORM_TRACE`  ·  =1

Central switch for platform-layer diagnostics. Off by default so release runs (and the byte-exact judge) never pay for stderr noise or hot-path instrumentation. Enable with the PAKU_PLATFORM_TRACE=1 environment variable or by setting  directly from a host.

### `src/PakuPort.Core/Platform/VideoState.cs`

#### `PAKU_LOG_CRTC`  ·  =1 / =2

PAKU_LOG_CRTC=2: traza cada escritura de registro CRTC y cada cambio de modo (diagnóstico de modos reprogramados a mano por el juego).

Usada en: `src/PakuPort.Core/Platform/VideoState.cs:115`, `src/PakuPort.Core/Platform/VideoState.cs:1156`, `src/PakuPort.Core/Platform/VideoState.cs:1363`

#### `PAKU_LOG_PALETTE`  ·  =1

PAKU_LOG_PALETTE=1: traza la paleta EFECTIVA — el RGB que acaba en pantalla para cada índice de color del juego (índice -> Attribute Controller -> DAC -> DacExpand) — cada vez que cambia, con el número de frames que llevaba puesta la anterior. Existe porque un juego que carga imágenes con su propia tabla de color reprograma la paleta ANTES de pintar cada una: la paleta buena para una imagen concreta no es "la del final de la partida" sino la que estaba puesta mientras esa imagen se veía, y sin traza hay que adivinar cuál era. Sale por stderr en hex para poder parsearla.

#### `PAKU_LOG_PRESENT`  ·  =1

PAKU_LOG_PRESENT=1: mide el RITMO REAL de presentación. Un frame que llega tarde o que se pierde se ve como un tirón, y con una escena en movimiento (un simulador de vuelo) un ritmo irregular se percibe como parpadeo. Reporta cada segundo cuántas capturas se hicieron, cuántas descartó el acelerador y la distribución de los intervalos.

#### `PAKU_PRESENT_CAPTURE`  ·  =1

Capture the current VRAM as the presented frame ("front buffer"). Called on the GAME thread by the clock-driven VGA refresh tick (MachineTimer, ~70 Hz real time) and once before an INT 16h blocking wait. No-op unless the host drives the display, so it never runs in the deterministic verifier path. Capture is CLOCK-DRIVEN and ASYNCHRONOUS to the game's cursor cycle — that is the whole point, and it mirrors how a real VGA CRTC scans VRAM out to the monitor on its own clock. Do NOT capture at the INT 33h AH=03 poll. Measured on a test game in the oracle (DOSBox): the software cursor is a save-under sprite whose move cycle is HIDE (restore background) then SHOW, and SHOW *polls INT 33h AH=03 and only AFTER that draws the sprite*. So at the instant of the poll the cursor is ERASED, not "freshly drawn" — capturing at the poll snapshots the one frame per redraw that is guaranteed cursorless, which is exactly the move-flicker the user reported. The cursor is present in VRAM the overwhelming majority of the time (redraw is rare and brief — invisible even to a PC-sampling profiler on the oracle), so an async wall-clock snapshot almost never lands in the tiny HIDE->SHOW gap, exactly like the oracle, which shows no flicker. Throttle only coalesces a refresh tick that coincides with the INT 16h pre-park capture. SIN acelerador: el ritmo lo marca el tick de refresco, que ya es periódico por su propio acumulador de tiempo real (MachineTimer). Hubo aquí un throttle de 10 ms medido con Environment.TickCount64 y era un colador: la resolución de ese reloj en Windows es ~15,6 ms, MAYOR que el periodo VGA de 14,286 ms, así que dos refrescos consecutivos caían a menudo en la misma ranura del reloj y el segundo se TIRABA — ~10 % de los frames perdidos y el intervalo p90 al doble del nominal (60 capturas/s con 9-11 descartadas, medido con tools/present_rate_test). En un menú quieto no se nota; con la imagen en movimiento es un tirón varias veces por segundo. Y ya había mordido antes: por él se quedaba sin presentar el menú terminado antes del park de INT 16h (ver Int16hHandler). Nada que coalescer: la ruta del park sólo captura en el modo legacy. PAKU_PRESENT_CAPTURE=1: captura los fotogramas presentados SIN ventana. Hasta aquí, la captura colgaba entera de , que sólo lo pone la plantilla del host con ventana. Consecuencias, las dos medidas: FrameGeneration se quedaba congelado a cero en cualquier corrida de captura —así que el ORDEN DE DIBUJO, que es lo que decide si un sprite va delante o detrás, había que estimarlo por huecos de tiempo— y PAKU_PRESENT_DUMP no podía volcar nada, así que el juez de fidelidad no podía correr solo. Va en una bandera APARTE y no reutilizando HostDrivenRetrace a propósito: aquélla además conmuta el retrace del VGA a reloj de pared (VgaPortHandler), y el juez necesita justo lo contrario — que las dos corridas de la misma cinta caigan en los mismos puntos del programa. Encenderla aquí habría hecho no determinista lo que se quería comparar.

#### `PAKU_PRESENT_DUMP`  ·  presencia o valor / valor de texto

PAKU_PRESENT_DUMP=<prefijo>: vuelca los frames PRESENTADOS (uno por captura, que es justo lo que ve el usuario) como RGBA crudo, para poder MIRAR una secuencia en vez de fiarse de un solo pantallazo. Arranca a los PAKU_PRESENT_DUMP_AFTER_MS y para tras PAKU_PRESENT_DUMP_N ficheros. Existe porque un parpadeo sólo se diagnostica viendo frames CONSECUTIVOS: un fotograma suelto de un juego que redibuja toda la pantalla no distingue "va bien" de "presentamos estados intermedios del dibujado".

Usada en: `src/PakuPort.Core/Platform/VideoState.cs:888`, `src/PakuPort.Core/Platform/VideoState.cs:963`

#### `PAKU_PRESENT_DUMP_AFTER_FRAMES`  ·  numero

PAKU_PRESENT_DUMP_AFTER_FRAMES=N: empieza a volcar tras N fotogramas CAPTURADOS en vez de tras N milisegundos de reloj de pared. Hace falta para comparar dos corridas de la misma cinta. Bajo `PAKU_TAPE_REPLAY` la CAPTURA ya es determinista (MachineTimer acumula tiempo VIRTUAL cuando el reloj está anclado), pero la puerta del volcado seguía siendo un cronómetro de pared: las dos corridas volcaban momentos distintos de la partida y salían distintas aunque el juego hubiera hecho exactamente lo mismo. Medido: con 40 s coincidían (el juego estaba en una pantalla quieta) y con 20 s no — o sea, el juez decía "el remake toca al juego" cuando lo único que pasaba era que miraba dos instantes diferentes.

#### `PAKU_PRESENT_DUMP_AFTER_MS`  ·  numero

PAKU_PRESENT_DUMP=<prefijo>: vuelca los frames PRESENTADOS (uno por captura, que es justo lo que ve el usuario) como RGBA crudo, para poder MIRAR una secuencia en vez de fiarse de un solo pantallazo. Arranca a los PAKU_PRESENT_DUMP_AFTER_MS y para tras PAKU_PRESENT_DUMP_N ficheros. Existe porque un parpadeo sólo se diagnostica viendo frames CONSECUTIVOS: un fotograma suelto de un juego que redibuja toda la pantalla no distingue "va bien" de "presentamos estados intermedios del dibujado".

#### `PAKU_PRESENT_DUMP_EVERY`  ·  numero

PAKU_PRESENT_DUMP_EVERY=N: volcar uno de cada N fotogramas en vez de los N primeros seguidos. Sirve para MUESTREAR una partida entera con un coste acotado, que es lo que necesita el kit de remakes para poder recortar la referencia de un asset del fotograma REAL en que se veía — reconstruirla desde índices + paleta obliga a acertar cuál de las (254, en un minuto de un juego de prueba) paletas del fundido tocaba, y no hay regla que acierte siempre.

#### `PAKU_PRESENT_DUMP_N`  ·  numero

PAKU_PRESENT_DUMP=<prefijo>: vuelca los frames PRESENTADOS (uno por captura, que es justo lo que ve el usuario) como RGBA crudo, para poder MIRAR una secuencia en vez de fiarse de un solo pantallazo. Arranca a los PAKU_PRESENT_DUMP_AFTER_MS y para tras PAKU_PRESENT_DUMP_N ficheros. Existe porque un parpadeo sólo se diagnostica viendo frames CONSECUTIVOS: un fotograma suelto de un juego que redibuja toda la pantalla no distingue "va bien" de "presentamos estados intermedios del dibujado".

#### `PAKU_PROC_PIXEL`  ·  =1

PAKU_PROC_PIXEL=1: además de contar, RECUERDA por cada byte de plano qué trozo de código lo escribió el último. Con eso se reconstruye, píxel a píxel, quién pintó la pantalla — y eso es lo que permite sustituir el dibujo de UNA rutina dejando encima, intacto, lo que pintaron las demás: en un juego 3D, cambiar el fondo sin borrar los sprites de encima. Cuesta dos bytes por byte de VRAM (512 KB) y sólo se reserva con la palanca puesta. Los ids son de 16 bits desde que existe la procedencia de RAM (ver Memory.ProcDeLin): ahí consume un id cada bloque que escribe CUALQUIER byte de la RAM alta, no sólo los que pintan, y 254 ids se desbordaban seguro — con todo fundido en el id compartido el mapa lleva a conclusiones falsas.

#### `PAKU_PROC_PIXEL_EN`  ·  numero

PAKU_PROC_PIXEL_EN=<segundos>: vuelca el mapa de procedencia EN ESE INSTANTE y no al salir. Hace falta porque al cerrar el juego suele estar en un menú o en la pantalla de carga, y el mapa sale dominado por lo que pintó esa pantalla en vez de por el juego.

#### `PAKU_TRAZA_EIP_VRAM`  ·  =1

PAKU_TRAZA_EIP_VRAM=1: cuántas escrituras a la VRAM hace cada trozo de código, por EIP redondeado a 256 bytes. Es el primer paso de la PROCEDENCIA DE PÍXEL: saber qué píxeles de la pantalla pintó cada rutina, que es lo que permite sustituir el dibujo de UNA de ellas dejando intacto lo que las demás pintan encima. En un juego 3D de prueba hace falta para separar el fondo —que dibuja su motor 3D— de los sprites que el juego pinta después: la separación por paleta NO sirve (está medido: el mapa de color del fondo usa los 256 índices, así que no queda ninguno libre con el que reconocer un sprite).

### `src/PakuPort.Core/RastroDeterminismo.cs`

#### `PAKU_RASTRO`  ·  valor de texto

`PAKU_RASTRO=<fichero>` — EL RASTRO DE UNA CORRIDA, para saber DONDE empiezan a diferenciarse dos que deberian ser la misma. Para que existe. Comparar el estado FINAL de dos corridas dice que difieren, no por que: cuando la memoria del invitado sale con unos bytes distintos, el sintoma esta a millones de instrucciones de la causa. Este rastro anota, cada 4096 instrucciones, donde estaba el programa y con que registros. Un `diff` de los dos ficheros da la PRIMERA instruccion en que las dos corridas dejan de ser la misma, y ahi si se puede mirar que leyo el juego. El eje es , que avanza igual en las dos corridas, asi que las lineas se corresponden una a una mientras no haya divergencia — no hace falta alinear nada. Coste apagado: cero en el camino caliente. Viaja en el poll periodico de , que ya se tomaba; apagado es una lectura de un campo estatico.

#### `PAKU_RASTRO_MEM`  ·  valor de texto

`PAKU_RASTRO_MEM=<lineal hex>:<bytes>[,...]`: unos bytes del invitado en cada linea del rastro. Los registros dicen DONDE esta el programa; esto dice en que ESTADO esta la partida (la fase, el modo de control, un contador), que es lo que hace falta para saber si una cinta llega a donde se queria.

### `src/PakuPort.Core/RemakeMap.cs`

#### `PAKU_REMAKE_GRACE_MS`  ·  numero

Guest-time windows in ms; converted to ticks with the governor's CyclesPerMs (20000 fallback when running free). Grace tunable for manual-playtest diagnosis.

## Plantilla del host — valen en el `Jugar<Juego>.exe` publicado

### `src/PakuPort.Cli/Program.cs`

#### `PAKU_AUTOEXIT_MS`  ·  numero / presencia o valor

Verificación automatizada (CI/diagnóstico, genérico): PAKU_NOSPLASH=1     salta el splash (la lógica arranca ya) PAKU_AUTOEXIT_MS=N  cierra tras N ms reportando estado + screenshot.png

Usada en: `src/PakuPort.Cli/Program.cs:1107`, `src/PakuPort.Cli/Program.cs:2836`, `src/PakuPort.Core/CorteDeterminista.cs:35`

#### `PAKU_AUTOKEYS`  ·  valor de texto

PAKU_AUTOKEYS (verificacion): una tecla cada ~10 frames; el make se MANTIENE 5 frames antes del break (la clase de juegos que escanean que teclas estan pulsadas AHORA).

Usada en: `src/PakuPort.Cli/Program.cs:1116`, `src/PakuPort.Cli/Program.cs:2844`

#### `PAKU_CLICKS`  ·  valor de texto

PAKU_CLICKS: clicks scriptados deterministas ""x,y,frame[,hold];..."" (coordenadas de JUEGO). En el frame indicado el cursor salta a (x,y) y pulsa botón izq `hold` frames (por defecto 6; hold=0 = mantener pulsado hasta el cierre, útil para dejar un menú desplegado en el screenshot). Mientras hay un click activo tiene prioridad sobre el wiggle y el ratón real.

#### `PAKU_CYCLES`  ·  numero

PAKU_CYCLES: override en runtime del governor (0 = velocidad libre). El valor horneado viene de game.json (cyclesPerMs = instrucciones/ms, la unidad DOSBox cycles=N).

Usada en: `src/PakuPort.Cli/Program.cs:1010`, `src/PakuPort.Cli/Program.cs:2641`

#### `PAKU_ESCALA_ENTERA`  ·  =1

Escala ENTERA: la imagen se amplia un numero exacto de veces y se centra. Sin esto el factor sale fraccionario (320x240 a 1080p son x4,5) y con filtro de vecino mas proximo unas filas se duplican y otras no: se ve como si media pantalla estuviera a media resolucion, y el patron BAILA cuando el juego mueve la imagen. Ctrl+F12 conmuta.

Usada en: `src/PakuPort.Cli/Program.cs:1045`, `src/PakuPort.Cli/Program.cs:2787`

#### `PAKU_FB_PNG`  ·  valor de texto

PAKU_FB_PNG=<fichero>: volcado del framebuffer NATIVO (WxH de juego, sin escala ni aspecto) — la fuente pixel-exacta para jueces y catalogos (el screenshot de ventana sale reescalado y no sirve para recortar sprites 1:1).

Usada en: `src/PakuPort.Cli/Program.cs:1484`, `src/PakuPort.Cli/Program.cs:3727`

#### `PAKU_INTERP`  ·  presencia o valor

Intérprete de respaldo: al caer en un hueco del código lifteado se ejecutan los bytes ORIGINALES de la imagen en memoria. PAKU_INTERP=0 lo desactiva (comportamiento Fase 0: registrar el hueco y volver mudo).

#### `PAKU_INTERP_ONLY`  ·  =1

Entrada del hilo de lógica. PAKU_INTERP_ONLY=1 corre el juego 100% en el intérprete (semántica 8086, el lifteado ni se toca); default = llamada al entry lifteado.

#### `PAKU_LIFTED`  ·  presencia o valor

Motor por defecto: interpOnly=true corre el juego 100% en el interprete (semantica 8086 real). PAKU_INTERP_ONLY=1 lo fuerza siempre; con interpDefault horneado, sin env vars = interprete y PAKU_LIFTED=1 vuelve al mundo lifteado de siempre. INTERP_DEFAULT se hornea desde game.json. Y si el remake trae el PROGRAMA entero traducido (FuncionesPortadas.RegistraPrograma), el motor es el interprete: es donde vive el gancho de las traducciones.

#### `PAKU_LIVE_VRAM`  ·  =1

PAKU_LIVE_VRAM=1 presenta siempre la VRAM viva en vez del front buffer capturado por reloj (diagnóstico / A/B del parpadeo). Por defecto se presenta el front buffer (modelo hardware).

#### `PAKU_MEMDUMP`  ·  valor de texto

PAKU_MEMDUMP=<fichero>: vuelca el MiB de memoria VIVA al terminar. Es la única forma de leer el código que un juego genera o descomprime en runtime (el de la imagen del EXE ya no es el que corre) y de ver dónde ha dejado sus vectores: así se localizó el driver de teclado de un juego de prueba, colgado de INT 15h y no del INT 9.

Usada en: `src/PakuPort.Cli/Program.cs:1470`, `src/PakuPort.Cli/Program.cs:3710`, `src/PakuPort.Core/CorteDeterminista.cs:77`

#### `PAKU_MEMDUMP_ALTO`  ·  valor de texto

PAKU_MEMDUMP_ALTO=<fichero>: la memoria PLANA (por encima de 1 MiB). En un juego de 32 bits todo lo que importa vive ahi —lo que descomprime al cargar un mapa, sus tablas—, y PAKU_MEMDUMP solo ve el MiB de abajo: buscar ahi y no encontrar nada se lee como «no esta» cuando lo que pasa es que no se ha mirado donde es.

Usada en: `src/PakuPort.Cli/Program.cs:1477`, `src/PakuPort.Cli/Program.cs:3717`

#### `PAKU_MOUSE_ABS`  ·  presencia o valor

Movimiento crudo del ratón con la captura activa: referencia del frame anterior y acumulador de las fracciones de píxel de juego. PAKU_MOUSE_ABS=1 vuelve al modelo previo (mickeys derivados de la posición ya recortada), por si algún juego prefiriera el anterior.

#### `PAKU_MOUSE_WIGGLE`  ·  valor de texto

PAKU_MOUSE_WIGGLE=1: movimiento sintético en coordenadas de juego (prueba headless del camino de eventos INT 33h sin ratón físico); =2: además clicks.

#### `PAKU_NOAUDIO`  ·  =1

SIN dispositivo de audio (PAKU_NOAUDIO) se escribe SOLO lo que el anillo tenia, sin rellenar con silencio hasta completar el bufer. La rama de arriba SI rellena, y debe: alli hay un dispositivo esperando un bloque entero cada vez. Aqui no hay nadie a quien alimentar, y rellenar convertia el WAV en un reloj de FOTOGRAMAS en vez de un reloj de AUDIO: la duracion del volcado salia proporcional a los fps del juego. Medido — un juego a ~59 fps daba 247 s de WAV para 45 s de partida (2665 bufers de 2048 = 122.880 muestras/s) mientras otro, a ~11 fps, daba 45,0 s clavados y parecia que el que estaba roto era el primero. El 84,9 % de silencio de aquel volcado era este relleno, no el juego.

Usada en: `src/PakuPort.Cli/Program.cs:1052`, `src/PakuPort.Cli/Program.cs:2731`, `src/PakuPort.Core/Platform/Machine/Mpu401.cs:172`

#### `PAKU_NOSPLASH`  ·  =1

Verificación automatizada (CI/diagnóstico, genérico): PAKU_NOSPLASH=1     salta el splash (la lógica arranca ya) PAKU_AUTOEXIT_MS=N  cierra tras N ms reportando estado + screenshot.png

Usada en: `src/PakuPort.Cli/Program.cs:1106`, `src/PakuPort.Cli/Program.cs:2835`

#### `PAKU_NO_ASPECT`  ·  presencia o valor

Escala y letterbox por frame: cubre cambio de modo de vídeo, F11 y redimensionado. Corrección de aspecto (DOSBox `aspect=true`): un CRT de la época mostraba TODOS estos modos a 4:3 con píxeles NO cuadrados, así que 320x200 y 640x200 hay que estirarlos en vertical (los círculos salen ovalados si no). Ctrl+F11 conmuta; PAKU_NO_ASPECT=1 la apaga. A 4:3 CRT shows EVERY mode at 4:3: displayed ratio w/(h*pixelAspect) == 4/3, so pixelAspect = 0.75*(w/h): 1.2 for 320x200 (DOSBox aspect=true), 0.6 for 320x400 double-scan, 1.35 for 720x400 text. The old reciprocal (4/3)/(w/h) squashed 320x200 to 1.92:1 (mild, went unnoticed) and blew 320x400 up to 0.48:1 (measured on a test game).

Usada en: `src/PakuPort.Cli/Program.cs:1024`, `src/PakuPort.Cli/Program.cs:1040`, `src/PakuPort.Cli/Program.cs:2694`, `src/PakuPort.Cli/Program.cs:2782`

#### `PAKU_NO_DPMI`  ·  presencia o valor

con EXTENSOR EMBEBIDO, que arrancan en modo real y preguntan por un host antes de intentar el cambio de modo por su cuenta. VA ANTES DE AttachMemory A PROPOSITO: es alli donde nace el host DPMI y se registra su INT 31h. PAKU_NO_DPMI=1 lo apaga para un A/B.

#### `PAKU_NO_GAMEPORT`  ·  =1 / presencia o valor

del usuario sin joystick físico). PAKU_NO_GAMEPORT=0 fuerza el puerto presente para A/B.

Usada en: `src/PakuPort.Cli/Program.cs:2627`, `src/PakuPort.Core/Platform/Machine/GamePort.cs:44`

#### `PAKU_NO_MOUSE_CAPTURE`  ·  =1

Captura de ratón estilo DOSBox: click dentro captura (cursor confinado a la imagen, el del JUEGO es el único visible, 1:1 con DOS), Ctrl+F10 libera, F11 = pantalla completa sin bordes. PAKU_NO_MOUSE_CAPTURE=1 la desactiva; una corrida sin ventana nunca captura.

#### `PAKU_NO_TYPEMATIC`  ·  presencia o valor

PAKU_NO_TYPEMATIC=1: escotilla por si algún juego se atraganta con las repeticiones.

Usada en: `src/PakuPort.Cli/Program.cs:1115`, `src/PakuPort.Cli/Program.cs:2871`

#### `PAKU_REMAKE_NO_AGUJEREAR`  ·  =1

PAKU_REMAKE_NO_AGUJEREAR=1: la textura HD tapa TODO, como antes del arreglo que destapo una prueba manual. Es el CONTROL NEGATIVO del juez de pantalla presentada (judge_presentado.py): sin una forma de reproducir el fallo a voluntad, un juez que sale verde no ha demostrado que sepa ponerse rojo. No se usa para jugar: congela la imagen.

#### `PAKU_SCREENSHOT_CADA`  ·  numero

PAKU_SCREENSHOT_CADA=<s>: captura de la pantalla COMPUESTA (juego + capas HD del remake) cada s segundos, a logs/pantalla-<n>.png. Es la única forma headless de VER lo que el jugador vería: PAKU_FB_PNG vuelca el frame nativo del juego, sin la composición.

#### `PAKU_TRACE_EX`  ·  =1

La pila C# ES la cadena de llamadas del juego (Fn_xxxxx) — con ella un opcode-no-soportado se diagnostica sin arqueología.

Usada en: `src/PakuPort.Cli/Program.cs:1221`, `src/PakuPort.Cli/Program.cs:3353`, `src/PakuPort.Cli/Program.cs:3360`

#### `PAKU_WAV_DUMP`  ·  valor de texto

PAKU_WAV_DUMP=<ruta>: vuelca el PCM drenado a un WAV mono 16-bit 22050. Es la unica forma OBJETIVA de decir si suena algo y qué (el oido no vale de juez); sin dispositivo de audio se crea el anillo igual para que el volcado funcione headless.

Usada en: `src/PakuPort.Cli/Program.cs:1072`, `src/PakuPort.Cli/Program.cs:2750`

#### `PAKU_WINDOWED`  ·  presencia o valor

Pantalla completa (ventana sin bordes) DE SERIE: el juego se escala y centra solo — viewScale se recalcula cada frame contra el tamaño real de la ventana —, así que no hay nada que recolocar al arrancar. F11 vuelve a ventana; PAKU_WINDOWED=1 arranca en ventana. El tamaño calculado arriba sigue siendo el que queda al salir del borderless. Excepción headless (CorteDeterminista.SinVentana): la ventana está oculta a propósito y cambiarle el estado hace que GLFW la muestre y robe el foco al usuario en mitad de un smoke.

Usada en: `src/PakuPort.Cli/Program.cs:1033`, `src/PakuPort.Cli/Program.cs:2712`

#### `PAKU_XBRZ`  ·  numero

PAKU_XBRZ=<2..6>: escalado xBRZ del frame del juego (sustituye a Scale3x si está puesto).
