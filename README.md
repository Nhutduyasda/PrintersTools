# PrinterFixTool - Công Cụ Tự Động Chẩn Đoán & Sửa Lỗi Máy In Windows

Công cụ dòng lệnh viết bằng **C# (.NET 8)** chuyên sâu dành cho Quản trị viên Hệ thống (System Administrators) và Kỹ thuật viên IT để tự động phát hiện, chẩn đoán, quét tìm IP mới và khắc phục sự cố máy in văn phòng trên hệ điều hành Windows 10 và Windows 11.

---

## 🚀 Tính Năng Chính

### 1. Quét WMI, Tự động gỡ Offline & Tự Động Quét Tìm IP Mới (Module 1)
- **Lọc máy in thực tế:** Tự động nhận diện và loại trừ các máy in ảo hệ thống (`OneNote`, `Microsoft Print to PDF`, `XPS Document Writer`, `Fax`).
- **Tự động ép Online:** Kiểm tra cờ `WorkOffline`, tự động can thiệp qua WMI (`printer.Put()`) để chuyển máy in từ trạng thái `Offline` về `Online` ngay lập tức.
- **Phát hiện lỗi cảm biến phần cứng:** Cảnh báo các lỗi vật lý như kẹt giấy (`Paper Jam`), hết giấy (`No Paper`), mở nắp (`Door Open`), hoặc hết mực (`No Toner`).
- **Kiểm tra kết nối mạng (Ping) & Tự động dò tìm IP mới (Port 9100 RAW/JetDirect):**
  - Thực hiện gửi gói tin `Ping` (timeout 2000ms) để xác thực kết nối vật lý.
  - **Tự động quét mạng LAN (Port 9100) khi Ping thất bại:**
    - Tự động bóc tách Subnet dải mạng (ví dụ `192.168.1.x`).
    - Quét đồng thời toàn bộ dải IP `.1` đến `.254` qua TCP Port 9100 bằng cơ chế đa luồng song song với timeout 280ms (hoàn thành quét 254 IP trong ~1-2 giây).
    - Tự động loại trừ IP của máy tính đang chạy và các IP của máy in khác đang hoạt động trong hệ thống.
    - **Xử lý an toàn tránh gán nhầm:**
      - *Nếu không có máy nào mở port 9100:* Báo đỏ kết luận máy in đang tắt nguồn, tuột dây LAN hoặc khác VLAN.
      - *Nếu chỉ có 1 IP mới duy nhất:* Tự động cập nhật IP này vào Port bị mất kết nối.
      - *Nếu phát hiện từ 2 IP trở lên:* Liệt kê danh sách kèm hostname và cho người dùng chọn số thứ tự, tránh gán nhầm sang máy in phòng ban khác.
    - Cập nhật đồng thời qua **WMI** và ghi trực tiếp vào **Registry** (`HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\{portName}`).

### 2. Làm sạch Print Spooler & Hàng Đợi In (Module 2)
- Dừng an toàn dịch vụ `Spooler` (`spoolsv.exe`). Trong trường hợp dịch vụ bị treo cứng, công cụ tự động kích hoạt `taskkill /F /IM spoolsv.exe` để giải phóng tiến trình.
- Dọn dẹp triệt để các file lệnh in bị lỗi/kẹt (`*.spl`, `*.shd`, `*.tmp`) trong thư mục `%SystemRoot%\System32\spool\PRINTERS`.
- Khởi động lại dịch vụ `Spooler` để Windows nạp lại cấu hình IP mới và đưa dịch vụ về trạng thái `Running`.

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
