# Hamul - Prototipo de audio (Fase 1)

Valida lo más crítico: mezclar tu micrófono con clips y mandarlo a un micrófono virtual.

## Requisitos
1. Windows 10/11
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
3. [VB-Cable](https://vb-audio.com/Cable/) (cable de audio virtual). Instálalo y reinicia el PC.

## Pasos
1. Crea la carpeta `sounds` junto a `Program.cs` y pon 1 a 9 archivos `.mp3` o `.wav` (propios).
2. En una terminal dentro de esta carpeta:
   ```
   dotnet run
   ```
3. Elige tres dispositivos cuando te lo pida:
   - **Micrófono REAL**: tu micrófono físico.
   - **Cable virtual**: `CABLE Input (VB-Audio Virtual Cable)`.
   - **Monitoreo**: tus audífonos.
4. Usa las teclas **1-9** para reproducir, **S** para parar todo, **Q** para salir.

## Probarlo en Discord o Dota
- En Discord: Ajustes de voz -> Dispositivo de entrada -> `CABLE Output (VB-Audio Virtual Cable)`.
  Desactiva la supresión de ruido (Krisp) y el control automático de ganancia.
- En Dota 2: pon `CABLE Output` como micrófono predeterminado de Windows y verifica en los ajustes de voz del juego que use ese dispositivo.
- Para probar sin juego: Configuración de Windows -> Sonido -> Entrada, y habla/reproduce mirando el medidor de `CABLE Output`.

## Qué validar en esta fase
- Que se escuche tu voz y el clip a la vez en el otro lado.
- Que no haya cortes ni eco (si hay cortes, sube la latencia de 50 a 100 en `WasapiOut`).
- Que el clip se oiga bien en tus audífonos (monitoreo).

## Siguiente fase
Atajo global con tecla mantenida y simulación de la tecla push-to-talk.
