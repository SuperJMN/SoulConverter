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
El DMC sólo tiene un mando de tono —el divisor de reloj—, así que **cuántas
afinaciones distintas usa el driver mide cuánto le importa el tono al
compositor**. Eso da una gradación:

| Afinaciones de una misma muestra | Qué es | Cómo se toca |
|---|---|---|
| una | un tambor fijo | bombo, caja o charles |
| dos, separadas ≥2 semitonos | un tambor **afinado** | la afinación principal sigue siendo el pulso; las desviaciones son timbales |
| tres o más | un **instrumento** | notas reales al tono que les toca |

Esa comprobación necesita ver el tema entero, no la muestra, así que vive en el
análisis global y no en las reglas.

El escalón del medio importa más de lo que parece. El *Overworld* de Super Mario
Bros. 3 toca su muestra de bombo una cuarta más grave durante tres golpes: es un
redoble de timbal, y el oyente lo echa de menos si no está. Aisladas, las dos
variantes son la misma grabación, miden idéntico y puntúan idéntico (bombo 6.5,
timbal 4.5 las dos), así que ninguna regla sobre la forma de onda puede
separarlas. Sólo el arreglo lo dice.

Y hay que tocarlo como lo que es: un bombo de chiptune **barre** el tono hacia
abajo, que es justo lo que lo hace sonar a bombo — y justo lo que borra la
afinación de un timbal. Un timbal mantiene su tono y **resuena**: la muestra de
Mario 3 sigue al 16 % de su pico después de un cuarto de segundo, y recortada a
su transitorio es un clic. Se reconstruye en el canal wave, con el timbre sacado
de la propia grabación y el volumen bajando a mano por la cola, porque ese canal
no tiene envolvente por hardware.

En cuanto una muestra pasa la prueba melódica deja de ser percusión y se toca
como una nota real. En el tema medido (275 disparos, 80 % del tema con el DMC
sonando) eso sube la correlación en la banda de 40-100 Hz de 0.28 a 0.65.

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

### Un canal libre no es un registro libre

Que un canal esté callado no significa que quepa cualquier cosa en él. El
enmascaramiento y la disonancia ocurren **entre frecuencias, no entre canales**:
un bombo reconstruido como cuadrada barrida de 403 a 183 Hz es, se llame como se
llame, una nota grave. Puesto en un pulse libre mientras el triángulo camina el
bajo a 196 Hz, no se oye como un tambor que encontró sitio; se oye como el bajo
tocando una nota falsa, porque eso es lo que hace el oído con dos tonos cuadrados
a un semitono.

Así que además del coste de expropiación se cobra un **coste de registro**: un
candidato con tono paga por acercarse a menos de una octava de otra voz que esté
sonando, y paga el peor instante, no la media —un batido se oye en cuanto ocurre,
diluirlo entre los frames limpios lo abarataría—. La ventana se extiende cuatro
frames a cada lado del golpe, porque el oído agrupa por cercanía en el tiempo y
un golpe grave metido en el hueco entre dos notas del bajo sigue leyéndose como
parte del bajo. Se excluye la voz del propio canal anfitrión: a esa se la
sustituye, no se compite con ella.

El ruido no tiene tono y por eso no puede desafinar con nadie: eso es lo que lo
convierte en el sitio natural de un golpe grave cuando el registro está ocupado.
El refuerzo en un segundo canal, que es un lujo, se descarta ante cualquier
choque por pequeño que sea.

Es el mismo error de siempre en otra magnitud: el modelo medía ocupación de canal
cuando el fenómeno es de banda.

### El bajo descansa sin chasquear

El triángulo del NES calla entre casi todas las notas y cambia de nota sin
articular: su secuenciador **no se reinicia nunca**, y eso es justo lo que hace
que la línea de bajo suene continua. Traducir eso al Game Boy tiene dos trampas,
y la conversión caía en las dos.

La primera: `$400B` marca el triángulo como "re-disparado", pero lo que recarga
es el contador lineal, **no la fase**. Traducirlo a un disparo de `NR34` sí
reinicia la wave RAM del Game Boy, que vuelve a su primera muestra —el pico del
triángulo—, así que la salida salta ahí desde donde estuviera: un clic en cada
nota. Ahora CH3 se arranca una vez y no se vuelve a disparar; los cambios de nota
escriben el periodo y nada más.

La segunda: para callar se apagaba el DAC (`NR30`), que es el *pop* clásico del
canal wave del DMG. Ahora se baja el nivel de salida a cero (`NR32`), que silencia
sin mover el DAC — y como nada deshabilita el canal, volver de un silencio
tampoco necesita disparo. En un tema de medio minuto: **de 51 disparos y 27
apagados de DAC a 1 y 1**.

Medido sobre el bajo aislado, contra el triángulo del NES aislado:

```
   banda        antes    ahora
  250-500 Hz    1.13     0.99
 500-1000 Hz    1.21     1.03
   1-2 kHz      1.52     1.14   <- los clics vivían aquí
   2-5 kHz      1.37     1.12
```

El fundamental nunca estuvo mal (1.00 y 1.02): lo que sobraba era el ataque que
el original no tenía. El sintetizador de referencia reiniciaba la fase igual que
el hardware, pero trataba las dos formas de callar como equivalentes, así que
ninguna métrica global lo veía: sólo aparece al aislar el canal y compararlo con
su original.

### Resultado sobre material real

El BGM del runner de RetroSharp: 60 disparos, 4 muestras distintas.

```
   preset    bytes      hits   attacks  envelope  spectral
      off    13109      0/60     0.763     0.555     0.982
     safe    13637     59/60     0.830     0.693     0.986
 balanced    13895     59/60     0.822     0.709     0.986
     full    14054     59/60     0.830     0.721     0.986
```

El golpe que falta es el blip de nivelación del DAC del driver: se descarta a
propósito. Ninguna de las tres estrategias activas roba un solo frame de
melodía en este tema. **La tabla ordena por parecido a otro chip, no por lo bien
que suena: elige de oído.**

Las cifras globales no lo enseñan, pero el redoble de timbal del compás 15 sí:
en su banda (110-170 Hz) `balanced` pasó de 703 a 1855 al reconocerlo, contra
1889 del original. Tres golpes de sesenta no mueven una media, y aun así son lo
primero que se echa en falta. Por eso las métricas aquí son informe y no examen.

El bombo de este tema es tonal, así que hasta que se cobró el registro se metía
en la banda del bajo: **15 de sus 23 golpes acababan en un pulse como cuadrada
grave, y 9 sonaban encima del triángulo**, en un caso a 183 Hz contra 196 Hz, un
semitono. Cobrando la banda quedan 5 en el pulse y **ninguno solapa con el bajo**,
en ninguna de las tres estrategias. El exceso de energía media (250-600 Hz), que
es donde se oía la cuadrada haciendo de bajo, baja de 1.42 a 1.34 en `balanced` y
de 1.57 a 1.39 en `full`.

El otro caso, un tema comercial cuyo DPCM es la línea de bajo (275 disparos, el
DMC sonando el 80 % del tiempo, cuatro grabaciones tocadas a cinco velocidades):

```
   preset    bytes      hits   attacks  envelope  spectral
      off    52699     0/275     0.891     0.605     0.960
     safe    52648     2/275     0.887     0.604     0.960
 balanced    65131   231/275     0.875     0.689     0.968
     full    65914   274/275     0.868     0.719     0.968
```

Aquí `safe` casi no puede hacer nada, porque un bajo no cabe en el canal de
ruido: su promesa es no tocar la melodía, y cumplirla significa quedarse quieta.
Por bandas, contra la referencia con su DPCM sonando:

```
   banda        off     safe  balanced     full
 40-100 Hz    0.510    0.510     1.027    1.103    <- el bajo muestreado
100-170 Hz    0.910    0.910     1.221    1.216    <- el triángulo
170-400 Hz    1.175    1.175     1.350    1.377
  400-1 kHz   1.445    1.444     1.634    1.653
```

El bajo muestreado pasa de faltar la mitad a estar entero. Que el resto del
espectro se pase de largo es el precio de reconstruir con generadores lo que era
una grabación: una cuadrada tiene más armónicos que la muestra que sustituye.

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

245 tests. Los que cubren la quinta voz están en `test_dpcm.py` (decodificador,
rasgos y clasificador), `test_allocator.py` (coste, expropiación, choque de
registro y restauración) y `test_variants.py` (una salida por estrategia, todas
importables, y `off` byte a byte igual que antes de que existiera nada de esto).

## Trabajo futuro

- Soporte de chips de expansión (VRC6 es el más plausible: son dos pulses más).
- Formas de onda de wave RAM alternativas al triángulo, por tema.
- El 14 % de energía que aún sobra en 1-2 kHz del bajo aislado, procedente de los
  silencios: bajar `NR32` a 0 salta de la onda al cero, mientras que el triángulo
  del NES al callar se congela en su último valor. Un fade escalonado
  (100 → 50 → 25 → 0) lo suavizaría a costa de añadir cola a las notas.

## Licencia

MIT. Ver [LICENSE](LICENSE).

El repositorio contiene **sólo código**. No se versiona ni se distribuye material
derivado de ROMs comerciales: los fixtures de los tests son VGM de NES sintéticos
generados por `vgm_nes2gb/testing.py`, y las conversiones de temas reales se
quedan en tu máquina.
