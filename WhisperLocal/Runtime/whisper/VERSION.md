# Bundled whisper.cpp runtime

Prebuilt whisper.cpp for Windows x64 with the **Vulkan** backend (`GGML_VULKAN=1`).
Taken from `whisper.cpp-windows-vulkan.zip` (file timestamps 2026-03-15), the build that
already worked on an AMD Radeon RX 6800 XT. The binaries carry no version resource, so the
exact whisper.cpp commit is unknown; the hashes below identify this known-good build.

Vulkan is vendor-neutral: the same files run on AMD, NVIDIA and Intel GPUs with a Vulkan driver.
On a different PC only the GPU index (`-dev N` in `Whisper.ExtraArgs`) may need to change;
`whisper-server.log` lists the detected Vulkan devices at startup.

Only the files WhisperLocal needs are kept (`whisper-bench.exe` is for speed tests).

| File | Size | SHA256 |
|---|---|---|
| `ggml-base.dll` | 545792 | `858C7A5D9D0CFF650F0A95402F037E309F7801D2B02A85B8D9ED3E46BC4CBF41` |
| `ggml-cpu.dll` | 748032 | `3CB448A73DC63DCFC2CB274A2CE1274DA7B1C23622DA837B264ACEDBBA297348` |
| `ggml-vulkan.dll` | 56708096 | `17A63146D6AA1618758D756D36D6B5D6A59F77FC2F72C8444F23ED1E54D14B53` |
| `ggml.dll` | 67584 | `42A2480BF6C5205549756AA5EED3C77DC9334809F40A1000895C944CA8F7F41F` |
| `whisper-bench.exe` | 20992 | `0C3028E1AB4A946AB9DF73604C542C0ABEF1934E39E0B84078A30674A7D7A6A2` |
| `whisper-server.exe` | 726528 | `310E1A91234D5BF5FDDF1B64758A55334C3DE6033B204882538144D8E0F0D3BB` |
| `whisper.dll` | 1307648 | `C27FE2C033D1299613FCB048DC01D74024D96DA4A9D6D6E223F900EEEBA99DE7` |

## Rebuilding from source (only if a newer whisper.cpp is needed)

Requires Visual Studio Build Tools (C++), CMake and the Vulkan SDK:

```
git clone https://github.com/ggml-org/whisper.cpp
cd whisper.cpp
cmake -B build -DGGML_VULKAN=1
cmake --build build --config Release
```

Copy `whisper-server.exe`, `whisper.dll` and `ggml*.dll` from `build\bin\Release` here and update the table.
