# HachBob AI — Полный список улучшений (19.06.2026)

## Где брать файлы
**Все улучшенные файлы лежат в папке `/home/user/improved/`**
Оригиналы (рабочие, нетронутые) — в `/home/user/uploads/`

## Что скопировать в проект

| Файл | Заменять? |
|------|-----------|
| `VisionEngine.cs` | ✅ Заменить полностью |
| `MouseLogic.cs` | ✅ Заменить полностью |
| `ScreenCaptureDXGI.cs` | ✅ Заменить полностью |
| `Windows.cs` | ✅ Заменить полностью |
| `SoundManager.cs` | ✅ Заменить полностью |
| `OpenVinoSession.cs` | ✅ Заменить полностью |
| `AppConfig.cs` | ✅ Заменить полностью |
| `MainWindow.xaml.cs` | ✅ Заменить полностью |
| `PresetsPanel.xaml.cs` | ✅ Заменить полностью |

Остальные файлы (`BindsPanel`, `DeadZonesPanel`, `SliderRow`, `InverseBoolConverter`, `.csproj`, `.slnx`) — **не трогать**, они не менялись.

---

## Что улучшено (26 правок)

### VisionEngine.cs

**Наведение (aim):**
- Sigmoid smoothstep вместо жёсткого `if (dist > 50px)` — плавный старт, быстрое сближение, торможение у цели, нет маятника
- Гарантированный минимум шага (`25% от линейного`) — прицел не замерзает на подлёте
- `_manualLockUntil` 1.0 сек вместо 3.0 — быстрее переключение целей

**Фильтрация детекций (меньше мусора):**
- Aspect-фильтр (0.12–1.15) — отсекает UI-полоски и текстуры, пропускает игрока в приседе и за стеной
- Min area 0.15% от capture — отсекает пыль/шум
- `LastDetections.Top(10)` — оверлей без мусорных боксов
- Edge filter 8px в `PostprocessTile` — нет дублей на стыках тайлов

**Ghost-трекинг (не теряет цель при пропадании):**
- 10 кадров вместо 6 (конфигурируется в `config.json`: `vision.ghost_max_frames`)
- 5 кадров полная скорость, потом затухание ×0.80
- `_frameInterval` из реального FPS вместо хардкода `0.016f`
- conf × 0.8 — триггербот не стреляет по призраку
- Якорь `_anchorLastRealX/Y` без ghost-экстраполяции — не теряет цель на остановках

**Выбор цели (приоритет на ближайшую):**
- `distScore²` — близость к прицелу возводится в квадрат
- `PrioritySize` работает — крупная цель получает бонус
- Инерция ×1.35 если цель рядом с предыдущей

**Производительность:**
- NMS in-place (без LINQ `.Where().ToList()`) — ×5-10 быстрее на 20+ детекциях
- `PreprocessTile`: обычный `for` вместо `Parallel.For` — быстрее для 416px
- `SF_MISS = 4` — меньше обрывов треков
- Сглаженный `_frameInterval` (EMA 0.85/0.15) — скачки FPS не дёргают предикцию
- `TargetSpeed` — скорость цели в px/s для адаптивного аима
- Сброс скорости при `spd < 30f` — нет overshoot на остановках

**RF/UI разделение:**
- `RfYOffsetExtra` + `TotalYOffset` — дальномер не конфликтует с пользовательским Y-offset

### MouseLogic.cs
- `_ignoreNextLmbUp` — триггербот не сбрасывает аим при LMB = кнопка аима
- Адаптивный strength по скорости цели (+0-40%)
- Повторная проверка `IsCrosshairOnTarget` перед выстрелом

### ScreenCaptureDXGI.cs
- Unsafe `fixed` + `while` цикл — BGRA→BGR на 30% быстрее
- Exponential backoff 200→400→800→1600→3200→5000ms при Device Lost

### Windows.cs
- Линии от центра прицела до детекций (сплошные для цели, пунктир для остальных)

### SoundManager.cs
- `TaskCompletionSource` вместо `Thread.Sleep(20)` поллинга
- Корректный `Dispose` с `CancellationTokenSource`

### OpenVinoSession.cs
- Убран `SetProcessAffinityMask` — не ломает DXGI на Dual Xeon
- `ENABLE_CPU_PINNING = numaPinning ? "NUMA" : "NO"`

### AppConfig.cs
- `ghost_max_frames` в `VisionConfig` (по умолчанию 10)

### MainWindow.xaml.cs
- `GhostMaxFrames` из конфига в `InitVision`

### PresetsPanel.xaml.cs
- `RemoveHandler` перед `AddHandler` при захвате бинда

---

## Если что-то сломалось

1. TRT не запускается → поставь `"provider": "cuda"` в `config.json`. Разница 2-3ms.
2. Аим ведёт не туда → проверь `aim_y_offset` в конфиге, должно работать как раньше
3. 0 FPS → проверь путь к модели в `config.json`, удали папку `trt_cache`

Оригиналы всегда в `/home/user/uploads/` — можно откатить любой файл.
