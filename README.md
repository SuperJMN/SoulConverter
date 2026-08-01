# vgm-nes2gb

Convierte temas de NES en formato VGM a temas de Game Boy en formato VGM.

Los dos chips no son equivalentes, así que la conversión no es una traducción de
registros: la herramienta **emula el APU del 2A03**, extrae el estado *musical*
resultante (qué nota, con qué volumen, con qué timbre, en cada instante) y lo
re-sintetiza como escrituras de registros del DMG.

```
IN.vgm/.vgz ─▶ vgm_reader ─▶ nes_apu ─▶ nes_to_gb ─▶ gb_vgm_writer ─▶ OUT.gb.vgm
                                │           │             │
                                └───────────┴─────────────┴─▶ render + analyze ─▶ informe
```

## Uso

```bash
# Un fichero
python3 -m vgm_nes2gb convert tema.nes.vgz -o tema.gb.vgm

# Con informe JSON, WAVs de referencia y espectrograma comparativo
python3 -m vgm_nes2gb convert tema.nes.vgz -o out/tema.gb.vgm --report out --wav --spectrogram

# Un directorio entero
python3 -m vgm_nes2gb convert temas/ -o convertidos/

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

Código de salida: `0` correcto (aunque haya avisos), `2` si algún fichero de un
lote falló, `1` si falló todo.

## Mapeo de canales

| NES 2A03 | Game Boy DMG | Notas |
|---|---|---|
| Pulse 1 (`$4000-$4003`) | CH1 square (`$FF10-$FF14`) | duty 1:1 (12.5/25/50/75 %) |
| Pulse 2 (`$4004-$4007`) | CH2 square (`$FF16-$FF19`) | duty 1:1 |
| Triangle (`$4008-$400B`) | CH3 wave (`$FF1A-$FF1E` + wave RAM) | triángulo de 32 pasos cargado en wave RAM |
| Noise (`$400C-$400F`) | CH4 noise (`$FF20-$FF23`) | vecino más cercano sobre la rejilla `(shift, divisor)` |
| DMC / DPCM (`$4010-$4013`) | — | **se descarta**, se contabiliza en el informe |

El sweep del NES, el envelope, el length counter y el linear counter se resuelven
en la emulación; al GB le llega ya el resultado. El sweep del hardware del GB se
deja desactivado (`NR10 = 0x00`) a propósito.

## Qué se pierde, y por qué

- **DPCM.** El Game Boy no tiene canal de muestras. Un tema que confía en el DPCM
  para la batería sonará sin percusión grave. El informe dice cuántas escrituras y
  cuántos bytes de muestra se han descartado.
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

Ejemplo real (el BGM del runner de RetroSharp, 28,6 s):

```
channel     active   notes  retrig  transp    cents  on/off
pulse1         891      79     667       0      0.8    1.00
pulse2         922      81     687       0      2.1    1.00
triangle       963      51      51       0      0.9    1.00
noise           81      76      76       0    111.9    1.00

envelope correlation : 0.974
spectral correlation : 0.981
```

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

## Trabajo futuro

- Sustituir la percusión de DPCM por percusión sintética sobre CH4.
- Soporte de chips de expansión (VRC6 es el más plausible: son dos pulses más).
- Formas de onda de wave RAM alternativas al triángulo, por tema.
