# Bluetooth Battery Bar

Widget para la barra de tareas de Windows que muestra, junto a la bandeja del sistema, los dispositivos inalámbricos conectados y la batería que les queda. La barra se ensancha según el número de dispositivos y aparece en todas las pantallas.

## Qué dispositivos muestra

- **Bluetooth**: cualquier dispositivo conectado que informe de su batería a Windows (auriculares, teclados, ratones…).
- **Logitech por receptor USB o cable** (Bolt, Unifying, Lightspeed): batería leída con el protocolo HID++ de Logitech.
- **Mandos Xbox** (adaptador inalámbrico Xbox o Bluetooth): nivel en cuatro pasos (llena, media, baja, vacía), que es lo que da Windows.

## Instalación

1. Descarga `BluetoothBatteryBar.exe` desde [Releases](../../releases/latest).
2. Ábrelo con doble clic y responde «Sí» a la pregunta de instalación.

Se copia a `%LOCALAPPDATA%\BluetoothBatteryBar`, arranca con Windows y no necesita permisos de administrador. Al no estar firmado, Windows puede mostrar el aviso de SmartScreen: «Más información» → «Ejecutar de todas formas».

Requisitos: Windows 11, o Windows 10 versión 1903 o posterior.

## Uso

- **Pasar el ratón por encima**: nombre del dispositivo y batería.
- **Clic izquierdo**: abre la configuración de Bluetooth.
- **Clic derecho**: actualizar, activar o desactivar el inicio con Windows, desinstalar y salir.

Se oculta cuando hay una aplicación a pantalla completa en ese monitor.

## Compilar

No hace falta instalar nada: usa el compilador de C# incluido en Windows.

```powershell
.\build.ps1            # genera dist\BluetoothBatteryBar.exe
.\build.ps1 -Install   # compila e instala o actualiza en este equipo
```

Diagnóstico de lo que detecta: `BluetoothBatteryBar.exe --dump salida.txt`.
