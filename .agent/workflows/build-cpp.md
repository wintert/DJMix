---
description: Build the C++ DJAudioEngine and copy DLL to C# project
---
# Build C++ DJ Audio Engine

## Steps

1. Open a terminal in `c:\Apps\DJApp\DJAudioEngine\build`

// turbo-all
2. Run the build command:
```
cmd /c "call ""C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\Tools\VsDevCmd.bat"" -arch=x64 && cd /d c:\Apps\DJApp\DJAudioEngine\build && ninja"
```

3. Copy the DLL to the C# project:
```
Copy-Item "c:\Apps\DJApp\DJAudioEngine\build\bin\DJAudioEngine.dll" -Destination "c:\Apps\DJApp\DJApp\bin\Debug\net10.0-windows\" -Force
```

## Notes
- The build uses Visual Studio 2018 Professional developer environment
- ninja is used as the build system (CMake generated)
- DLL output is in `DJAudioEngine\build\bin\DJAudioEngine.dll`
- DLL needs to be copied to `DJApp\bin\Debug\net10.0-windows\` for the C# app to use it
