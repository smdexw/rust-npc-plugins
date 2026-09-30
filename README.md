# Rust NPC 插件開發環境

操作與開發紀錄：[Rust伺服器與NPC開發紀錄](outputs/Rust伺服器與NPC開發紀錄.md)。

- 啟動：雙擊 `Start-Server.cmd`。
- 本機 Rust 遊戲按 F1：`connect 127.0.0.1:28015`。
- 關服：伺服器主控台輸入 `server.save`，接著 `quit`。
- 更新：關服並備份後雙擊 `Update-Server.cmd`。
- 部署插件：`scripts/Deploy-Plugin.ps1 <插件名稱>`，會把 `plugins/src/<名稱>.cs` 複製到 `server/oxide/plugins/`。

安裝與測試狀態請見 [安裝驗證結果](outputs/安裝驗證結果.md)。

## 插件

| 插件 | 說明 |
| --- | --- |
| `NpcStarter.cs` | 護衛 NPC：跟隨、戰鬥、採集、製作、F6 管理選單；按 E 打開護衛的背包與裝備。使用方式見 [NPC插件使用說明](outputs/NPC插件使用說明.md)。 |
| `GatherRates.cs` | 採集倍率，見 [資源倍率設定](outputs/資源倍率設定.md)。 |
| `NpcDevHello.cs` | 環境驗證用的最小插件。 |

## 使用的第三方插件

- [Recycle Manager](https://umod.org/plugins/recycle-manager)（作者 WhiteThunder）：回收產出倍率。這個插件不是本專案的作品，所以沒有放進這個 repo；請從 uMod 下載後放到 `server/oxide/plugins/`。設定方式見 [資源倍率設定](outputs/資源倍率設定.md)。

伺服器使用 [Oxide/uMod](https://umod.org/) 載入插件。`server/`、`tools/`、`work/` 是本機的伺服器檔案、工具與暫存，不包含在 repo 內。
