# vgm-nes2gb

Convierte temas de NES en formato VGM a temas de Game Boy en formato VGM.

Los dos chips no son equivalentes, así que la conversión no es una traducción de
registros: la herramienta **emula el APU del 2A03**, extrae el estado *musical*
resultante (qué nota, con qué volumen, con qué timbre, en cada instante) y lo
re-sintetiza como escrituras de registros del DMG.

```
IN.vgm/.vgz ─▶ vgm_reader ─▶ nes_apu ─▶ nes_to_gb ─▶ gb_vgm_writer ─▶ OUT.gb.vgm
                                │  │        ▲  │             │
                                │  └─ dpcm ─┘  │             │
                                │     percussion              │
                                │     allocator               │
                                └───────────┴─────────────────┴─▶ render + analyze ─▶ informe
```

El Game Boy no tiene canal de muestras, así que el DPCM del NES se puede
**re-sintetizar sobre los cuatro generadores del DMG**. Es opcional y viene
apagado; consulta [La quinta voz](#la-quinta-voz-el-dpcm-en-los-otros-canales).

## Uso

```bash
# Un fichero
python3 -m vgm_nes2gb convert tema.nes.vgz -o tema.gb.vgm

# Con informe JSON, WAVs de referencia y espectrograma comparativo
python3 -m vgm_nes2gb convert tema.nes.vgz -o out/tema.gb.vgm --report out --wav --spectrogram

# Un directorio entero
python3 -m vgm_nes2gb convert temas/ -o convertidos/

# Reconstruye la batería de DPCM sobre los canales del Game Boy
python3 -m vgm_nes2gb convert tema.nes.vgz -o tema.gb.vgm --dpcm balanced

# Una versión por estrategia, para elegir de oído
python3 -m vgm_nes2gb convert tema.nes.vgz -o tema.gb.vgm --variants

# Genera un VGM de NES sintético para probar sin material con copyright
python3 -m vgm_nes2gb demo -o demo.vgm
```

Requiere Python 3.12 y `numpy`. `scipy` no es necesario. No hace falta
`matplotlib`: el espectrograma PNG se escribe a mano.

### Opciones

| Opción | Efecto |
|---|---|
| `--volume-mode retrigger` | (por defecto) volumen fijo por tick igual al del NES, re-disparando el canal cada vez que cambia. Sigue la dinámica con precisión, a costa de más bytes. |
| `--volume-mode envelope` | traslada la envolvente del NES a la unidad de envolvente del GB una sola vez por nota. Sale la mitad de grande y no chasquea. |
| `--tick-hz N` | resolución de la rejilla de conversión (60 Hz por defecto). Súbelo si el tema usa arpegios rápidos. |
| `--no-transpose` | recorta las notas por debajo del rango del GB en vez de subirlas una octava. |
| `--report DIR` | escribe un JSON por fichero con todas las métricas. |
| `--wav` | escribe el render del NES y el del GB para comparar de oído. |
| `--spectrogram` | escribe un PNG con los dos espectrogramas apilados. |
| `--dpcm off\|safe\|balanced\|full` | qué hacer con el canal de muestras. Por defecto `off`. |
| `--variants` | genera un fichero por estrategia (`tema.off.gb.vgm`, `tema.safe.gb.vgm`, …) y una tabla comparativa. Respeta la extensión que pidas: con `-o tema.vgz` salen cuatro `.vgz`. |
| `--dpcm-role ADDR=ROL` | corrige a mano la clasificación de una muestra, p. ej. `--dpcm-role 0xEA80=kick`. Repetible. |
| `--dpcm-dump DIR` | escribe un WAV por muestra decodificada para comprobar la clasificación de oído. |

La extensión de salida decide el formato: `.vgm` sale en claro y `.vgz` sale
**gzipeado de verdad**, que es lo único que distingue a los dos formatos.

Código de salida: `0` correcto (aunque haya avisos), `2` si algún fichero de un
lote falló, `1` si falló todo.

## Mapeo de canales

| NES 2A03 | Game Boy DMG | Notas |
|---|---|---|
| Pulse 1 (`$4000-$4003`) | CH1 square (`$FF10-$FF14`) | duty 1:1 (12.5/25/50/75 %) |
| Pulse 2 (`$4004-$4007`) | CH2 square (`$FF16-$FF19`) | duty 1:1 |
| Triangle (`$4008-$400B`) | CH3 wave (`$FF1A-$FF1E` + wave RAM) | triángulo de 32 pasos cargado en wave RAM |
| Noise (`$400C-$400F`) | CH4 noise (`$FF20-$FF23`) | vecino más cercano sobre la rejilla `(shift, divisor)` |
| DMC / DPCM (`$4010-$4013`) | los cuatro, según el reparto | **opcional**: apagado se descarta, encendido se re-sintetiza. Ver abajo. |

El sweep del NES, el envelope, el length counter y el linear counter se resuelven
en la emulación; al GB le llega ya el resultado. El sweep del hardware del GB se
deja desactivado (`NR10 = 0x00`) salvo cuando lo pide una receta de percusión, y
se limpia siempre al devolver el canal.

## La quinta voz: el DPCM en los otros canales

El NES tenía cinco canales y el Game Boy tiene cuatro. El que sobra es justo el
que no se puede traducir: el DMC reproduce muestras y el DMG no sabe hacerlo.
Como en la práctica ese canal casi siempre lleva **la batería**, tirarlo deja el
tema sin pegada.

La alternativa es no traducir el canal sino **lo que suena en él**: decodificar
cada muestra, medir qué es, y volver a tocarla con los generadores que sí tiene
el Game Boy. Un bombo es un cuadrado grave con barrido descendente; una caja es
ruido de banda media con caída rápida; un charles es ruido brillante de dos
frames. Ninguno de los tres necesita muestras.

Eso obliga a quitarle el canal a alguien durante unos frames, así que la
herramienta pone precio a ese robo y sólo paga cuando compensa.

```bash
python3 -m vgm_nes2gb convert tema.nes.vgz -o tema.gb.vgm --variants
```

| Estrategia | Qué hace |
|---|---|
| `off` | descarta el DPCM (por defecto). El informe sigue diciendo qué se pierde. |
| `safe` | percusión sólo sobre el canal de ruido. La melodía no se toca jamás. |
| `balanced` | percusión sobre el ruido, y batería o bajo muestreado en un canal tonal cuando está lo bastante libre. |
| `full` | además refuerza los golpes en un segundo canal y presta canales con más soltura. |

### Cómo decide qué es cada muestra

Cada muestra distinta se decodifica y se mide: duración efectiva, posición del
ataque, caída, reparto de energía en tres bandas, centroide, planitud espectral,
tono y su confianza, y deriva de tono. Un conjunto de reglas con nombre puntúa
cada rol, y los pesos negativos son vetos. El informe dice siempre **por qué**:

```
0xE980  257B x33 -> snare    (0.769)  ruido a 4681 Hz para un centroide de 1128 Hz durante 39 ms
   why: mid-band dominant, crack centroid, crack length, little bass, dies away
0xEA80  897B x26 -> kick     (0.625)  cuadrado con barrido desde 403 Hz durante 66 ms
   why: low-band dominant, bass pitch, short body, downward sweep, not bright
0xE000  129B x 1 -> artifact (1.000)  descartada
   why: peak at the tail, below the DMG's range
```

Si se equivoca, se corrige a mano con `--dpcm-role 0xE000=kick`, y el informe
marca la muestra como corregida. Con `--dpcm-dump DIR` obtienes un WAV por
muestra para juzgar tú.

### Cuando el DPCM no es la batería sino el bajo

Hay una segunda escuela de driver, la de *Journey to Silius* o *Gremlins 2*, que
usa el canal de muestras como **instrumento melódico**: una sola grabación de
bajo, retocada de tono cambiando la frecuencia de reproducción. Suena grave,
oscuro y percusivo, exactamente igual que un bombo, y por eso mirando la forma de
onda de un golpe suelto **no se pueden distinguir**.

La pista no está en el sonido, está en el arreglo: *nadie transpone un bombo*.
El DMC sólo tiene un mando de tono —el divisor de reloj—, así que una misma
dirección disparada a tres velocidades distintas o más, con tono medible, es una
escala. Esa comprobación necesita ver el tema entero, no la muestra, así que vive
en el análisis global y no en las reglas.

En cuanto una muestra pasa esa prueba deja de ser percusión y se toca como lo que
es: una nota real, al tono que le toca, con una onda derivada de la propia
grabación en el canal wave. En el tema medido (275 disparos, 80 % del tema con el
DMC sonando) eso sube la correlación en la banda de 40-100 Hz de 0.28 a 0.65.

Un efecto lateral: las notas más graves de un instrumento transpuesto caen por
debajo del suelo de detección de tono. Se recupera heredando el tono de una
hermana y escalándolo por la razón de velocidades, que es exacta porque el DMC es
un divisor.

La delta-modulación tiende dos trampas que costaron sangre y que conviene conocer
si se toca el clasificador:

- **Periodicidad artificial.** El filtrado del DPCM hace que hasta una caja dé
  confianza de tono de 0.87. Por eso un sonido que se apaga en menos de una
  décima de segundo se veta como tonal por muy periódico que parezca.
- **Dither obligatorio.** El formato no puede quedarse quieto: siguiendo algo más
  lento que dos escalones por muestra alterna arriba y abajo. La dirección
  muestra a muestra no dice nada, y el silencio codificado es un zumbido de
  16 kHz que parece un charles. Ambos se resuelven midiendo sobre la tendencia
  suavizada y descartando lo que no supera el escalón del DAC.

### Qué se cobra por robar un canal

El coste es perceptual, no aritmético. Silenciar una nota de bajo sostenida es
caro; silenciar un charles de un frame no lo es, porque el golpe que lo sustituye
lo enmascara de todos modos. Esa segunda medición es la que hace que quepa la
batería: en material real el canal de ruido del NES está ocupado un 5 % del tema,
y lo poco que toca cae exactamente sobre los golpes.

Se compara **coste por frame contra beneficio por frame**, y ese detalle importa
más de lo que parece: comparar el coste acumulado de N frames contra el beneficio
de un golpe hace que el veredicto dependa de cuánto dure la nota, así que ninguna
nota larga podría entrar jamás. El error pasa desapercibido mientras todo lo que
se coloca son golpes cortos y parecidos, y sale a la luz en cuanto aparece un
bajo sostenido.

Entre los canales que salen a cuenta se elige **el más barato**, no el primero
que se pueda pagar, con un margen de tolerancia para que la preferencia de timbre
siga mandando en los empates. Sin ese margen la herramienta arrasaba el triángulo
teniendo un pulse libre al lado.

Para saber si una ráfaga de ruido es decoración o una voz, la pregunta no es
cuánto dura sino **si decae desde su pico o se sostiene**: el NES lo escribe
explícitamente en el bit de envolvente. Una nota que se mantiene es una parte
musical y se respeta aunque sea corta.

Dos invariantes se cumplen siempre:

- **Todo canal prestado se devuelve.** Nunca se queda mudo ni con los ajustes del
  tambor. `NR10` se limpia (si no, desafina la siguiente nota) y la wave RAM del
  triángulo se recarga.
- **El DMC era monofónico**, así que cada voz se trunca en el siguiente disparo.
  Y ninguna nota puede durar más que su propia muestra, salvo que el original la
  hiciera bucle.

### Resultado sobre material real

El BGM del runner de RetroSharp: 60 disparos, 4 muestras distintas.

```
   preset    bytes      hits   attacks  envelope  spectral
      off    13226      0/60     0.778     0.555     0.982
     safe    13754     59/60     0.822     0.688     0.986
 balanced    13943     59/60     0.837     0.704     0.984
     full    14306     59/60     0.837     0.721     0.982
```

El golpe que falta es el blip de nivelación del DAC del driver: se descarta a
propósito. Ninguna de las tres estrategias activas roba un solo frame de
melodía en este tema. **La tabla ordena por parecido a otro chip, no por lo bien
que suena: elige de oído.**

El otro caso, un tema comercial cuyo DPCM es la línea de bajo (275 disparos, el
DMC sonando el 80 % del tiempo, cuatro grabaciones tocadas a cinco velocidades):

```
   preset    bytes      hits   attacks  envelope  spectral
      off    53758     0/275     0.900     0.604     0.960
     safe    53707     2/275     0.896     0.603     0.960
 balanced    62212   239/275     0.887     0.687     0.967
     full    63346   274/275     0.884     0.710     0.968
```

Aquí `safe` casi no puede hacer nada, porque un bajo no cabe en el canal de
ruido: su promesa es no tocar la melodía, y cumplirla significa quedarse quieta.
Por bandas, contra la referencia con su DPCM sonando:

```
   banda        off     safe  balanced     full
 40-100 Hz    0.278    0.279     0.557    0.649    <- el bajo muestreado
100-170 Hz    0.685    0.685     0.668    0.607    <- el triángulo
170-400 Hz    0.839    0.839     0.812    0.799
  400-1 kHz   0.870    0.870     0.856    0.847
```

Es el compromiso en crudo: el bajo se recupera y el triángulo paga por ello,
poco en `balanced` y algo más en `full`.

## Qué se pierde, y por qué

- **DPCM.** El Game Boy no tiene canal de muestras. Con `--dpcm off` (por defecto)
  el tema se queda sin percusión de muestras; con cualquier otra estrategia se
  reconstruye con los generadores del DMG, que se parece pero no es lo mismo: se
  recupera el ritmo y el ataque, no el timbre exacto de la grabación.
- **Los graves del triangle.** El canal wave del GB no baja de 32 Hz. Las notas por
  debajo se suben octavas enteras (y se avisa), o se recortan con `--no-transpose`.
- **La dinámica del canal wave.** CH3 solo tiene cuatro niveles (0/25/50/100 %) y
  no tiene envolvente. Un triangle con decaimiento marcado queda plano.
- **El ruido no es equivalente.** Las 16 frecuencias del NES no coinciden con la
  rejilla del GB. El informe da la desviación en cents por índice de periodo; en
  los extremos pasa de 100 cents y se nota.
- **Los efectos más rápidos que la rejilla.** A 60 Hz un arpegio que el motor
  original actualiza varias veces por frame se pierde en parte. `--tick-hz 240`
  lo recupera a costa de tamaño.
- **Chips de expansión.** VRC6, VRC7, FDS, MMC5, Namco 163 y Sunsoft 5B se
  rechazan con un error que nombra el chip. Solo se acepta el 2A03 base.

## Las métricas son un informe, no un aprobado

El informe mide error de pitch en cents por canal, correlación de envolvente,
correlación espectral y relación de RMS, comparando dos renders propios: un
sintetizador del 2A03 alimentado por el modelo del APU, y un sintetizador del DMG
que reproduce **los bytes del VGM emitido**, no el modelo intermedio.

Esos números están para decidir si el resultado te vale, no para bloquear nada. La
conversión nunca falla por una métrica. Dos chips distintos no pueden dar un render
igual, y perseguir la igualdad sería el objetivo equivocado: lo que importa es que
el tema se reconozca y suene bien.

Ejemplo real (el BGM del runner de RetroSharp, 28,6 s, con `--dpcm off`):

```
channel     active   notes  retrig  transp    cents  on/off
pulse1         891      79     667       0      0.8    1.00
pulse2         922      81     687       0      2.1    1.00
triangle       963      51      51       0      0.9    1.00
noise           81      76      76       0    111.9    1.00

envelope correlation : 0.555
spectral correlation : 0.982
```

La correlación de envolvente es baja **a propósito**: el render de referencia del
NES toca el DPCM, así que la diferencia mide exactamente el hueco que deja la
batería ausente. Reconstruirla lo sube a 0.72. Antes de que el render de
referencia fuese honesto, ese mismo número daba 0.974 y no significaba nada.

## Compatibilidad con RetroSharp

La salida está pensada para que la importe `RetroSharp.Core.Sdk.VgmImporter`, que
es estricto. El escritor se ciñe a ese subconjunto:

- Solo emite los comandos `0xB3`, `0x61`, `0x62` y `0x66`. La etiqueta GD3 va
  después del `0x66`, donde ningún consumidor la lee como comando.
- Declara el reloj DMG (`4194304`) en el offset `0x80` de la cabecera, y deja el
  slot del 2A03 (`0x84`) a cero.
- Solo escribe registros `0x00-0x16` (`$FF10-$FF26`) y `0x20-0x2F` (wave RAM). El
  rango `0x17-0x1F` no está soportado por el importador y el escritor lo rechaza.
- Traslada el punto de loop a `0x1C`/`0x20`.

`tests/test_gb_vgm_writer.py` fija ese contrato para que no se rompa por accidente.

## Tests

```bash
python3 -m unittest discover -s tests -t .
```

No se versiona nada derivado de una ROM comercial: los fixtures son VGM de NES
sintéticos generados en `vgm_nes2gb/testing.py`. Para pasar además la batería
sobre temas reales de tu máquina:

```bash
VGM_NES2GB_SAMPLES=~/mis-temas python3 -m unittest discover -s tests -t .
```

Sin esa variable, `tests/test_real_theme.py` busca el BGM del runner de RetroSharp
y, si tampoco está, se salta solo.

209 tests. Los que cubren la quinta voz están en `test_dpcm.py` (decodificador,
rasgos y clasificador), `test_allocator.py` (coste, expropiación y restauración) y
`test_variants.py` (una salida por estrategia, todas importables, y `off` byte a
byte igual que antes de que existiera nada de esto).

## Trabajo futuro

- Soporte de chips de expansión (VRC6 es el más plausible: son dos pulses más).
- Formas de onda de wave RAM alternativas al triángulo, por tema.
- Roles `tom` y `tonal` del clasificador: implementados, pero sin material real
  que los ejercite todavía.
