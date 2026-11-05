# PrinterFixTool - Công Cụ Tự Động Chẩn Đoán & Sửa Lỗi Máy In Windows

Công cụ dòng lệnh viết bằng **C# (.NET 8)** chuyên sâu dành cho Quản trị viên Hệ thống (System Administrators) và Kỹ thuật viên IT để tự động phát hiện, chẩn đoán và khắc phục sự cố máy in văn phòng trên hệ điều hành Windows 10 và Windows 11.

---

## 🚀 Tính Năng Chính

### 1. Quét WMI & Tự động gỡ Offline (Module 1)
- **Lọc máy in thực tế:** Tự động nhận diện và loại trừ các máy in ảo hệ thống (`OneNote`, `Microsoft Print to PDF`, `XPS Document Writer`, `Fax`).
- **Tự động ép Online:** Kiểm tra cờ `WorkOffline`, tự động can thiệp qua WMI (`printer.Put()`) để chuyển máy in từ trạng thái `Offline` về `Online` ngay lập tức.
- **Phát hiện lỗi cảm biến phần cứng:** Cảnh báo các lỗi vật lý như kẹt giấy (`Paper Jam`), hết giấy (`No Paper`), mở nắp (`Door Open`), hoặc hết mực (`No Toner`).
- **Kiểm tra kết nối mạng (Ping):**
  - Tự động map cổng mạng từ `Win32_TCPIPPrinterPort` và địa chỉ chia sẻ UNC LAN (`\\Server\Printer`).
  - Thực hiện gửi gói tin `Ping` (timeout 2000ms) để xác thực kết nối vật lý từ máy trạm đến máy in.
  - Cảnh báo chi tiết nếu bị mất kết nối và gợi ý hướng xử lý (kiểm tra nguồn, dây cáp, IP bị nhảy do DHCP).

### 2. Làm sạch Print Spooler & Hàng Đợi In (Module 2)
- Dừng an toàn dịch vụ `Spooler` (`spoolsv.exe`). Trong trường hợp dịch vụ bị treo cứng, công cụ tự động kích hoạt `taskkill /F /IM spoolsv.exe` để giải phóng tiến trình.
- Dọn dẹp triệt để các file lệnh in bị lỗi/kẹt (`*.spl`, `*.shd`, `*.tmp`) trong thư mục `%SystemRoot%\System32\spool\PRINTERS`.
- Khởi động lại dịch vụ `Spooler` và xác nhận trạng thái `Running`.

### 3. Vá Lỗi Registry Chia Sẻ Máy In Mạng LAN (Module 3)
- **Khắc phục lỗi `0x0000011b`:** Tự động cấu hình DWORD `RpcAuthnLevelPrivacyEnabled = 0` tại `HKLM\System\CurrentControlSet\Control\Print`.
- **Khắc phục lỗi `0x00000709`:** Tự động tạo subkey và cấu hình DWORD `RpcUseNamedPipeProtocol = 1` tại `HKLM\Software\Policies\Microsoft\Windows NT\Printers\RPC` (đặc trị lỗi chia sẻ máy in trên Windows 11 22H2+).

---

## 🛠️ Yêu Cầu Kỹ Thuật & Đóng Gói

- **Framework:** .NET 8.0 (`net8.0-windows`)
- **Kiến trúc:** Windows x64 (`win-x64`)
- **Quyền hạn:** Tích hợp `app.manifest` yêu cầu quyền `requireAdministrator` (tự động bật UAC prompt khi click đúp).
- **Phân phối:** Single-File, Self-Contained (máy đích không cần cài đặt .NET Runtime).

---

## 📦 Hướng Dẫn Biên Dịch & Đóng Gói (Publish Single-File)

Để xuất ra 1 file `.exe` độc lập duy nhất có thể chạy trên mọi máy tính Windows:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

File thực thi được tạo tại:
```text
bin\Release\net8.0-windows\win-x64\publish\PrinterFixTool.exe
```

---

## 💻 Cách Sử Dụng

1. Tải hoặc copy file `PrinterFixTool.exe` sang máy tính cần sửa lỗi máy in.
2. Click đúp chuột vào file `PrinterFixTool.exe`.
3. Khi hộp thoại **UAC** xuất hiện, chọn **Yes** để cấp quyền Quản trị (Administrator).
4. Công cụ sẽ tự động chạy qua 3 module và in ra bảng tổng hợp kết quả chi tiết.
5. Nhấn phím bất kỳ để kết thúc chương trình.

---

## 📄 Bản Quyền

Phát triển bởi [Nhutduyasda](https://github.com/Nhutduyasda).
Giấy phép MIT License.
