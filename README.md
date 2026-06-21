
# CV_demo — Real-time Object Detection Pipeline
<img width="509" height="313" alt="image" src="https://github.com/user-attachments/assets/07060a86-b22d-4c85-8372-0026a3d44cfa" />

## Star History
If you find this project useful, please ⭐ star the repo!
Native C# / .NET 8 / WPF implementation of a low-latency computer vision pipeline.  
Built for research and accessibility prototyping. No Python / no pip dependencies.

> ⚠️ **Disclaimer**  
> This project is provided strictly for **educational and research purposes**:  
> computer vision experiments, accessibility tools for motor-impaired users,  
> sports footage analysis, and low-latency input research.  
> The author is **not responsible** for any misuse of this software.

---

## Features

- **DXGI Desktop Duplication** capture (~0.5–1 ms latency) + GDI fallback
- **ONNX Runtime** inference with multiple backends:
  - NVIDIA: CUDA / TensorRT
  - AMD / Intel: DirectML
  - CPU: OpenVINO (Intel optimized)
- **YOLOv8** detector with NMS and tiled inference for high-resolution scenes
- **Ghost-target extrapolation** for occluded / lost detections
- **SendInput**-based pointer control (sub-millisecond latency)
- Adjustable smoothing, dead zones, FOV, prediction
- WPF UI with live overlay, presets, configurable hotkeys

---

## Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Optional GPU acceleration:
  - **NVIDIA**: CUDA Toolkit + cuDNN (+ TensorRT for max performance)
  - **AMD / Intel**: nothing extra — DirectML is built into Windows
  - **Intel CPU/iGPU**: OpenVINO Runtime

---

## Quick Start

```bash
git clone https://github.com/Koshmarjk/CV_demo
cd CV_demo
dotnet run -c Release
```

### Build standalone executable

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o ./dist
```

---

## Project Structure

```
CV_demo/
├── Vision/
│   ├── VisionEngine.cs          — ONNX inference + detection pipeline
│   └── ScreenCaptureDXGI.cs     — DXGI Desktop Duplication (~0.5ms) + GDI fallback
├── Input/
│   └── MouseLogic.cs            — SendInput + smoothing + auto-trigger
├── Config/
│   └── AppConfig.cs             — JSON config + presets
├── Audio/
│   └── SoundManager.cs          — NAudio
├── UI/
│   ├── Windows.cs               — Overlay + Indicator
│   ├── SliderRow.xaml/.cs       — Slider component
│   ├── BindsPanel.xaml/.cs      — Hotkey configuration
│   ├── DeadZonesPanel.xaml/.cs  — Dead zones
│   └── PresetsPanel.xaml/.cs    — Profile presets
├── MainWindow.xaml/.cs          — Main window
└── CV_demo.csproj
```

---

## Performance

| Component       | Method                      | Latency      |
|-----------------|-----------------------------|--------------|
| Screen capture  | DXGI Desktop Duplication    | ~0.5–1 ms    |
| Screen capture  | GDI BitBlt (fallback)       | ~5–15 ms     |
| Pointer input   | SendInput (atomic call)     | < 1 ms       |
| Inference (NV)  | ONNX + TensorRT             | ~2–5 ms      |
| Inference (NV)  | ONNX + CUDA                 | ~3–8 ms      |
| Inference (AMD) | ONNX + DirectML             | ~5–15 ms     |
| Inference (CPU) | ONNX + OpenVINO             | ~10–25 ms    |

---

## Files (next to executable)

```
model.onnx      — YOLOv8 detector (person class)
config.json     — auto-generated on first launch
presets.json    — auto-generated on first launch
```

---
## Models
https://drive.google.com/drive/folders/1iWcrr-2MuxFeKV1Sk2UKpiGuny-_qBMF?usp=sharing

## Default Hotkeys

| Key       | Action                  |
|-----------|-------------------------|
| Mouse 5   | Hold tracking           |
| Mouse 4   | Switch target           |
| INSERT    | Enable / Disable        |
| HOME      | Hide GUI                |
| V         | Dead zones toggle       |
| \         | Auto-trigger toggle     |
| F4        | Overlay                 |
| F12       | Exit                    |
| F1–F5     | Profile presets         |

---

## License

Educational use only. See `LICENSE` for details.
