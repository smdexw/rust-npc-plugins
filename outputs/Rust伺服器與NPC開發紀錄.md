# Rust 伺服器與 NPC 插件開發紀錄

建立日期：2026-09-25（Asia/Taipei）。安裝根目錄：`D:\RustServer`。本文件所在位置是專案的 `outputs` 子資料夾，以下路徑皆相對於專案根目錄。

## 用途與架構

本資料夾用於 Rust 遊戲的 Windows 專用伺服器與 NPC 插件開發。伺服器 App ID 是 **258550**，遊戲本體是 252490。插件採用 **Oxide / uMod、C#**；不是 Rust 程式語言的 Cargo 專案。

| 位置 | 用途 |
|---|---|
| `Start-Server.cmd` | 雙擊啟動本機開發伺服器 |
| `Update-Server.cmd` | 關服後更新 Rust，接著重新套用 Oxide |
| `tools/steamcmd/` | Valve 官方下載及更新工具 |
| `server/` | 專用伺服器與 Oxide 執行環境 |
| `server/server/npc-dev/` | 此開發世界的存檔與設定 |
| `server/server/npc-dev/cfg/server.cfg` | 伺服器設定及本機 RCON 密碼 |
| `server/logs/server.log` | 啟動與執行紀錄；下次啟動前可另存 |
| `plugins/src/` | 插件原始碼，正式開發請修改此處 |
| `server/oxide/plugins/` | 部署後由 Oxide 編譯及載入的插件 |
| `server/oxide/config/` | 插件設定 |
| `server/oxide/data/` | 插件持久資料 |
| `work/` | 安裝下載、版本資訊及測試暫存 |

## 啟動與連線

**2026-09-26 更新：已啟用 Rust 內建 PvE。** `server/server/npc-dev/cfg/server.cfg` 設為 `server.pve true`，運行中的伺服器也已即時套用並回報 `True`。這是 PvE 基本規則，NPC、環境危險與生存要素仍存在；若要更細的玩家傷害、建築保護或區域規則，需另訂設計。

1. 雙擊根目錄的 `Start-Server.cmd`，等候日誌出現 `Server startup complete`。首次建圖可能需要數分鐘。
2. 在同一台電腦啟動 Steam 版 Rust，按 F1 輸入 `connect 127.0.0.1:28015`。
3. 關閉伺服器時，在伺服器主控台依序執行 `server.save`、`quit`。

預設：僅本機 127.0.0.1、最多 5 人、地圖大小 1000、seed 12345、identity `npc-dev`。遊戲使用 UDP 28015、查詢使用 UDP 28017，RCON 使用本機 TCP 28016。RCON 密碼已隨機產生於 server.cfg，啟動工具會讀取並傳給伺服器。Rust 的啟動日誌與本機程序參數可能包含此密碼；不要公開設定、未遮蔽的日誌或提交版本控制。

小型地圖用來降低開發測試的資源需求，並不代表完整正式地圖的 NPC、道路與地標測試。Facepunch 列出 12 GB 可用 RAM 與 15 GB 可用磁碟需求；同機開遊戲還需額外資源。依使用者指定，安裝位置已改為 D:\RustServer；搬移前 D 槽剩約 58.6 GiB。請持續留意存檔、地圖及更新所需空間。

## 管理員與插件驗證

在伺服器主控台輸入以下命令，把範例換成自己的 SteamID64：

```text
ownerid YOUR_STEAMID64 "YourName"
server.writecfg
```

重新連線後，在遊戲聊天輸入 `/npcdev`，範例插件會回覆版本。`NpcDevHello` 只驗證插件環境，**尚未實作 NPC 生成、對話、交易或任務功能**。

## 開發流程

**2026-09-26 更新：**採集、新生成寶箱及回收機產出已設為 3 倍，詳見 [資源倍率設定](資源倍率設定.md)。

已新增可生成及互動的基礎插件 `NpcStarter`，指令與測試步驟見 [NPC 插件使用說明](NPC插件使用說明.md)。先前的 `NpcDevHello` 保留作環境驗證範例。

1. 在 `plugins/src/` 編輯 C# 插件，檔名與類別名稱一致。
2. 在專案根目錄開啟 PowerShell，執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Deploy-Plugin.ps1 -Name NpcDevHello
```

3. 查看伺服器主控台及 Oxide 日誌，確認編譯成功。需要時執行 `oxide.reload NpcDevHello`。
4. 記錄重現步驟、預期與實際結果、伺服器和 Oxide 版本。

NPC 插件建議先定義：用途（商人、任務、守衛、陪伴）、生成位置、權限、互動距離、資料保存方式。再逐步加入生成／移除、互動、對話及重啟後還原。處理插件卸載時的 NPC 與計時器清理，避免重載產生重複 NPC；確認交易扣款與發放不會重複執行。

正式交付玩家前，測試無權限玩家、重複互動、斷線重連、死亡、NPC 被殺、插件重載、伺服器重啟、wipe 與多人同時使用。API、prefab 與尋路行為應以已安裝版本實測，不要假設舊教學仍適用。

## 更新與備份

關服後先備份 `plugins/src/`、`server/server/npc-dev/` 和 `server/oxide/` 中的 config、data、plugins，再執行 `Update-Server.cmd`。更新腳本會驗證原版檔案，然後下載並覆蓋最新版 Oxide。若 Oxide 下載或相容性出問題，修復後再啟動供玩家使用。

Rust 更新可能覆蓋 Oxide，因此日常啟動不自動更新。程式與下載檔已列入 `.gitignore`，插件原始碼、操作工具和本文件可納入版本控制。尚未建立 Git 儲存庫。

## 開放玩家連線前

目前是本機開發環境。若之後要讓其他電腦連線，需要調整 `scripts/Start-Server.ps1` 的 `server.ip`，並依架設位置設定防火牆及路由器 UDP 28015/28017。RCON 維持本機限制，透過受控管理管道使用。這次未新增防火牆、路由器轉發或開機自動啟動設定。

## 來源

- [Facepunch：Creating a server](https://wiki.facepunch.com/rust/Creating-a-server)
- [Valve：SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD)
- [Oxide 安裝文件](https://docs.oxidemod.com/guides/owners/install-oxide)
- [Oxide.Rust 官方版本](https://github.com/OxideMod/Oxide.Rust/releases)

實際安裝版本及驗證結果另記於同目錄的「安裝驗證結果.md」。


## 本機遠端管理工具

若伺服器在背景執行，可在 D:\RustServer 開啟 PowerShell，使用以下工具（會從設定讀取密碼，不必手動貼上）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Send-Rcon.ps1 -Command "status"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Send-Rcon.ps1 -Command "server.save"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Send-Rcon.ps1 -Command "quit"
```

若尚未完成啟動、連接埠未開啟，或指令沒有回應，工具會在 15 秒後逾時。

