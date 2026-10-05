# Multi-Headset Audio Sharing

Aplikasi Windows berbasis C#, .NET 10, WPF, dan NAudio 3.1.0 stabil. Menangkap mix satu playback endpoint menggunakan WASAPI loopback shared mode, kemudian mengirimkannya ke output lain. Tidak memasang driver virtual dan tidak mengubah default output atau master volume Windows.

## Menjalankan

Prasyarat: Windows 10/11, .NET 10 SDK, perangkat audio sudah terhubung dan aktif di Windows.

Dari folder proyek:

```powershell
dotnet run --project App/App.csproj
```

Atau jalankan `Run.ps1`. Solution `MultiHeadsetAudioSharing.slnx` dapat dibuka melalui Visual Studio yang mendukung .NET 10. Build release:

```powershell
dotnet build MultiHeadsetAudioSharing.slnx -c Release
```

Hasil aplikasi berada di `App/bin/Release/net10.0-windows/MultiHeadsetAudioSharing.exe`. Folder build lengkap harus tetap bersama executable; hasil ini membutuhkan .NET 10 Desktop Runtime, bukan executable mandiri.

## Penggunaan

1. Pilih **sumber audio**. Arahkan WAV/browser atau aplikasi lain ke endpoint tersebut melalui pengaturan audio Windows. Audio yang menuju endpoint lain tidak termasuk capture.
2. Centang **2–5 perangkat pendengar aktif**. Perangkat inactive/disconnected tetap terlihat tetapi tidak dapat dipilih.
3. Tekan **Mulai berbagi**. Source dan pilihan pendengar dikunci sampai sesi berhenti; volume output redistribusi tetap dapat diubah.
4. Atur slider **0–100%** untuk setiap output redistribusi. Gain mengubah sampel pada reader output itu, bukan master volume perangkat Windows. Output yang dimute di Windows tetap tidak terdengar meskipun gain aplikasi 100%.
5. Tekan **Berhenti** untuk melepas capture/playback, mengganti pilihan, atau memasukkan perangkat yang baru tersambung kembali.

**Source tidak menerima playback ulang.** Source yang dicentang sebagai pendengar menggunakan playback asli Windows dan volumenya dikelola Windows, bukan slider aplikasi. Source yang tidak dicentang tetap dapat bersuara melalui Windows, tetapi tidak dihitung sebagai pendengar.

Untuk dua headset dengan kedua gain dikendalikan aplikasi, gunakan **endpoint ketiga sebagai source**, misalnya output laptop. Jangan menganggap mute source tidak memengaruhi loopback pada semua perangkat. Kebutuhan source sepenuhnya senyap dan volume aplikasi untuk semua listener dengan hanya dua endpoint fisik memerlukan desain routing terpisah; aplikasi ini tidak menjanjikannya.

## Perilaku engine

- Capture dipin ke satu source selama sesi; perubahan default Windows tidak mereroute sesi.
- PCM internal: 48 kHz, stereo, float32. WASAPI shared mode menangani konversi ke format output.
- Satu ring buffer terbatas 200 ms per output, dengan reader independen. Output lambat membuang frame tertuanya sendiri; underrun menghasilkan silence, bukan EOF. Buffer digunakan ulang pada callback audio.
- Seluruh output diinisialisasi sebelum sesi diterima sebagai berjalan. Kegagalan startup membatalkan sesi dan melepas resource yang sudah dibuka.
- Output redistribusi yang putus atau mengalami error dilepas sendiri; pendengar lain terus berjalan, termasuk ketika tersisa satu listener.
- Source hilang atau tidak ada listener tersisa: sesi dihentikan.
- Reconnect tidak auto-join; berhenti lalu pilih ulang.
- Meter input menahan peak selama maksimal 250 ms agar paket senyap sesaat tidak menutupi audio pada polling UI 200 ms; nilainya kembali nol saat idle. Buffer depth, frame underrun, dan frame terbuang ditampilkan. Underrun juga bertambah saat source idle: counter tersebut bukan pengukuran dropout audio terdengar.

## Struktur

- `App/`: XAML, window, binding dan model baris endpoint.
- `Core/AudioDeviceManager.cs`: enumerasi endpoint dan notifikasi Windows.
- `Core/AudioBufferManager.cs`: fan-out, reader independen, gain dan counter buffer.
- `Core/SessionCoordinator.cs`: lifecycle recorder/player, rollback, disconnect dan disposal.
- `Tests/`: executable pengujian perilaku buffer dan console smoke perangkat nyata; tanpa perangkat mock.

## Verifikasi

Pengujian deterministik, tanpa memutar audio:

```powershell
dotnet run --project Tests/Tests.csproj -c Release
```

Mencakup isolasi reader/gain, overflow reader lambat, paket melebihi kapasitas, wraparound, silence/resume, removal reader, frame alignment, serta gain tidak valid.

Enumerasi endpoint dan smoke lifecycle pada endpoint Windows nyata:

```powershell
dotnet run --project Tests/Tests.csproj -c Release -- --devices
dotnet run --project Tests/Tests.csproj -c Release -- --lifecycle-smoke
```

Lifecycle smoke memerlukan minimal dua endpoint aktif. Menguji validasi listener, startup stream nyata, perubahan gain, dan tiga siklus Start/Stop; **bukan bukti suara terdengar**.

Smoke sinyal audio, dengan tone 440 Hz beramplitudo rendah menuju source:

```powershell
dotnet run --project Tests/Tests.csproj -c Release -- --smoke
# Opsional: pilih ID endpoint yang ditampilkan oleh --devices.
dotnet run --project Tests/Tests.csproj -c Release -- --smoke --source "ID_ENDPOINT"
```

Smoke sinyal harus menerima sampel non-senyap, kemudian menguji sesi, gain, pause/resume pemutar uji, dan Stop. Gagal dengan error apabila sinyal tidak tertangkap; tidak dianggap lulus hanya karena callback datang. Karena loopback menangkap mix, audio aplikasi lain juga dapat masuk; pause pemutar uji tidak menjamin seluruh source idle. Perhatikan pengaturan audio Windows dan volume perangkat sebelum menjalankan karena smoke memutar tone. Tidak ada perubahan otomatis pada mute/master volume Windows.

### Hasil pemeriksaan implementasi

- Build release: lulus tanpa warning/error.
- Tujuh pengujian perilaku buffer: lulus.
- Lifecycle engine: validasi satu listener ditolak; tiga siklus dua output redistribusi, gain dan Stop lulus pada endpoint Windows nyata.
- WPF: window dijalankan, refresh dan pilihan source diperiksa, dua output dimulai, selection dikunci, gain diubah, Start/Stop diulang, dan meter input non-nol diamati. Shutdown ketika dua stream masih aktif selesai dengan exit code 0. Binding meter input, source selector setelah refresh, dan peak meter yang tertutup paket senyap diperbaiki berdasarkan observasi runtime.
- Smoke sinyal: loopback menerima PCM non-senyap saat pemutar uji aktif; tiga siklus dua output redistribusi, gain, pause/resume pemutar uji dan Stop lulus. Pemeriksaan awal ketika desktop terkunci menerima sampel senyap; setelah sinyal nyata tersedia, diagnosis callback membuktikan capture engine menerima audio. Tidak ada klaim suara kedua headset telah didengar secara fisik.
- Disconnect fisik, output busy/exclusive, 3–5 perangkat fisik, Windows 10, dan soak belum diverifikasi.

Untuk acceptance perangkat nyata: putar materi non-DRM ke source terpisah, dengarkan dua output redistribusi, mute gain A tanpa mengubah B, cabut A (B tetap berjalan), kemudian cabut source (sesi berhenti). Uji Start/Stop dan output busy, source idle/resume, 30 menit sesi MVP, lalu soak dua jam. Gunakan perekaman fisik sinyal klik untuk latency end-to-end; buffer depth bukan bukti latency speaker/headset.

Target PRD CPU <10% untuk tiga output, memory <300 MB, serta latency aplikasi <100 ms **belum diukur**. Latency Bluetooth dan drift clock jangka panjang berbeda dari latency aplikasi; belum ada koreksi rate adaptif per reader atau jaminan sinkronisasi.

## Batas MVP

Konten DRM dapat tidak tertangkap. Aplikasi bukan alat pairing Bluetooth dan tidak menjamin adapter/profil Bluetooth mampu memainkan 3–5 headset sekaligus. Manual delay, automatic calibration, profiles, per-application capture dan channel control belum termasuk MVP.

Referensi: [Microsoft WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), [NAudio 3.1.0](https://github.com/naudio/NAudio/tree/v3.1.0).
