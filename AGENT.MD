# PazeE Language — AI 接手指南 (AGENT.MD)

> 本文档为下一个 AI 助手提供项目上下文、关键结构、构建流程和常见问题速查。

---

## 1. 项目概览

| 项 | 值 |
|---|---|
| **项目名** | PazeE Language |
| **产品名** | PazeE 编程语言编译器 |
| **作者 / 账号** | JGZ_YES（GitHub: JGZYES） |
| **仓库** | https://github.com/JGZYES/PazeE-Language.git |
| **主版本** | 0.1.0-alpha（Alpha 阶段） |
| **License** | GPL-3.0（见根目录 LICENSE） |
| **框架** | .NET 10 / C# 最新版 |
| **编译器输出** | `paze.exe`（AssemblyName=paze，RootNamespace=PazeE.Compiler） |
| **安装器** | WiX Toolset v6.0.2（MSI + Bundle EXE） |

### Git 分支

| 分支 | 用途 |
|---|---|
| `main` | 集成开发（默认分支，最新代码） |
| `alpha-0.1` | Alpha 0.1 版本分支 |

### 最近提交（从旧到新）

```
83fe7bf  Initial commit: PazeE Language Alpha-0.1
844b4a4  installer: GPL3 license + WixUI_Mondo wizard + icon + ARP copyright
4d46b61  installer: branded bitmaps for professional UI
b1c7210  installer: switch to WixUI_InstallDir (Python-like 3-step flow)
08cef4f  installer: switch to WixInternalUIBootstrapperApplication
2ed6aa2  installer: multi-language support (zh-CN/en-us/zh-tw)
d5f9ecd  installer: fix banner/dialog_bg bitmap text overlap with WixUI
f4cf6f8  installer: move banner logo to right corner to avoid WixUI title overlap
41c19b0  installer: use new 500x500 logo for MSI and EXE
9fac7cb  installer: update logo to latest version (500x500, 46KB)
```

---

## 2. 目录结构

```
PazeE Language/
├── AGENT.MD                       ← 本文档
├── LICENSE                        ← GPL-3.0
├── logo.png                       ← 源 Logo (500x500, PNG 32bpp ARGB)
├── PazeE.slnx                     ← VS 解决方案（.slnx 新格式）
├── .gitignore / .gitattributes
├── publish/
│   └── 0.1.0-alpha/
│       ├── bin/                   ← 编译后的编译器二进制（自包含 .NET 10）
│       │   └── paze.exe + 运行时依赖
│       └── installer/
│           ├── PazeE-0.1.0-alpha-win-x64.exe   ← Bundle 安装器 (~29.8MB)
│           └── PazeE-0.1.0-alpha-win-x64.msi   ← MSI 安装包 (~29MB)
├── src/
│   ├── PazeE.Compiler/
│   │   ├── PazeE.Compiler.csproj  ← 编译器项目（.NET 10 Console）
│   │   ├── Program.cs             ← 入口（CLI）
│   │   ├── Diagnostics.cs
│   │   ├── Lexer/                 ← 词法分析（Lexer.cs, Token.cs, Preprocessor.cs）
│   │   ├── Parser/                ← 语法分析（Parser.cs, Ast.cs, Types.cs）
│   │   ├── Semantic/              ← 语义分析（Sema.cs, Symbol.cs）
│   │   ├── CodeGen/
│   │   │   ├── X64/               ← x86_64 代码生成（X64CodeGenerator, X64Emitter, Abi, X64Reg）
│   │   │   └── Arm64/             ← AArch64 代码生成（Arm64CodeGenerator, Arm64Emitter）
│   │   ├── Binary/                ← 可执行文件写入器
│   │   │   ├── IExecutableWriter.cs
│   │   │   ├── PeWriter.cs / PeWriterArm64.cs      ← Windows PE
│   │   │   ├── ElfWriter.cs / ElfWriterArm64.cs    ← Linux ELF
│   │   │   ├── ElfWriterLos4.cs / Los4Runtime.cs   ← LeonOS ELF
│   │   │   ├── MachOWriter.cs / MachOWriterArm64.cs ← macOS Mach-O
│   │   │   ├── ObjectImage.cs
│   │   │   └── leonos4/           ← LeonOS 用户态运行时（含头文件 + libc.a）
│   │   └── Runtime/               ← Libc 声明 + 启动 Stub
│   └── PazeE.Installer/
│       ├── PazeE.Installer.Msi.wixproj      ← MSI 项目
│       ├── PazeE.Installer.Bundle.wixproj   ← Bundle EXE 项目
│       ├── Product.wxs          ← MSI 的 WiX 源代码（安装逻辑、路径、UI）
│       ├── Bundle.wxs           ← Bundle 的 WiX 源代码（内嵌 MSI + BA）
│       ├── en-US.wxl            ← 英文本地化（32 条 UI 字符串）
│       ├── zh-CN.wxl            ← 简体中文本地化
│       ├── zh-TW.wxl            ← 繁體中文本地化
│       ├── license.rtf          ← GPL3 协议（LicenseAgreementDlg 用）
│       ├── logo.ico             ← 多尺寸图标（16/32/48/64/128/256）
│       ├── logo_large.png       ← Bundle BA Logo（128x128）
│       ├── banner.png           ← 对话框顶部横幅 (493x58)
│       └── dialog_bg.png        ← 欢迎/完成页背景 (493x312)
└── tests/
```

---

## 3. 构建 & 发布流程

### 3.1 前置条件

| 工具 | 版本 | 用途 |
|---|---|---|
| .NET SDK | 10.0+ | 编译 PazeE.Compiler（`dotnet` CLI） |
| WiX Toolset SDK | 6.0.2（NuGet） | 构建 MSI / EXE |
| WixToolset.Heat | 6.0.2 | 自动收割 publish/bin 目录为 MSI 组件 |
| WixToolset.UI.wixext | 6.0.2 | 提供 WixUI_InstallDir 向导对话框 |
| WixToolset.BootstrapperApplications.wixext | 6.0.2 | 提供 WixInternalUIBootstrapperApplication |
| gh CLI | 任意已登录 JGZYES 账号 | 可选：推送 GitHub 时用 |

> WiX 6.0.2 不要求独立安装 WiX Toolset，直接通过 NuGet SDK 还原即可（`<Project Sdk="WixToolset.Sdk/6.0.2">`）。

### 3.2 完整发布步骤（三阶段）

```
阶段 1: 编译编译器二进制 → publish/0.1.0-alpha/bin/
阶段 2: 生成 Logo 衍生文件（logo.ico / logo_large / banner / dialog_bg）
阶段 3: 构建 MSI + Bundle EXE → publish/0.1.0-alpha/installer/
```

**阶段 1 — 编译 paze.exe（自包含 x64）：**

```powershell
cd "f:\PazeE Language"
dotnet publish src/PazeE.Compiler/PazeE.Compiler.csproj -c Release `
  -p:BuildChannel=alpha -r win-x64 --self-contained true
# 输出: publish/0.1.0-alpha/bin/paze.exe + .NET 运行时（整体 ~80MB）
```

关键 csproj 属性（[PazeE.Compiler.csproj](src/PazeE.Compiler/PazeE.Compiler.csproj)）：
- `<BuildChannel>`：release / beta / alpha（默认 release）→ 影响版本号后缀
- `<PublishDir>`：`..\..\publish\$(Version)\bin\` → `publish/0.1.0-alpha/bin/`
- `GenerateBuildInfo` 目标自动生成 `BuildInfo.g.cs`（暴露 Channel 和 Version）

**阶段 2 — 从 logo.png 生成衍生文件（如果 logo.png 有更新）：**

使用 System.Drawing（PowerShell）：

```powershell
Add-Type -AssemblyName System.Drawing
$logo = [System.Drawing.Image]::FromFile((Resolve-Path "logo.png").Path)
# 1. logo.ico：16/32/48/64/128/256，用 PNG 嵌入 ICO 格式
#    （≥256 尺寸用 0 表示宽度/高度；ICO 结构: ICONDIR + ICONDIRENTRY[] + 各尺寸 PNG 数据）
# 2. logo_large.png：128x128，HighQualityBicubic 缩放
# 3. banner.png：493x58，Logo 放右下角 (435,3, 52x52)，底部 8px 渐变带
#    左侧必须留白 —— WixUI 会在 Banner 左侧渲染对话框标题（许可协议/目标文件夹等）
# 4. dialog_bg.png：493x312，左侧深色品牌区（0-180px）+ 分隔线(青色) + 右侧白色区(180-493)
#    左侧品牌区包含 Logo(20,20,140x140) + "PazeE" 文字 + 分隔线 + 说明文字 + 底部 paze 名
$logo.Dispose()
```

**阶段 3 — 构建安装器（MSI + EXE）：**

```powershell
# 只需构建 Bundle 项目 —— 它通过 <ProjectReference> 自动先构建 MSI 项目
dotnet build src/PazeE.Installer/PazeE.Installer.Bundle.wixproj -c Release
```

产物：
- `src/PazeE.Installer/obj/Release/{zh-cn,en-us,zh-tw}/*.msi`（MSI 多语言构建中间物）
- `publish/0.1.0-alpha/installer/PazeE-0.1.0-alpha-win-x64.msi`（默认 zh-cn 语言的 MSI 发布副本）
- `publish/0.1.0-alpha/installer/PazeE-0.1.0-alpha-win-x64.exe`（Bundle EXE，内嵌 MSI）

构建时长约 1–1.5 分钟（主要耗时在 MSI 的 heat 收割和压缩）。

### 3.3 推送 GitHub

大陆环境 github.com:443 通常不可直连，但系统代理一般可用（如 127.0.0.1:51081）：

```powershell
# 先找系统代理
Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" | Select ProxyEnable,ProxyServer
# 若 ProxyEnable=1 且 ProxyServer=127.0.0.1:51081，配置 git：
git config set http.proxy  "http://127.0.0.1:51081"
git config set https.proxy "http://127.0.0.1:51081"
# gh CLI 用 api.github.com（走自己的网络栈，通常可达）—— 但 git push 走 github.com:443，必须代理

git push -u origin main
git push -u origin alpha-0.1
```

本地身份（非全局，不改 ~/.gitconfig）：
```
git config set --local user.name  "JGZ_YES"
git config set --local user.email "luoriguodu@qq.com"
```

---

## 4. 安装器技术细节

### 4.1 两个项目共享一个目录

| 文件 | MSI 项目 (`*.Msi.wixproj`) | Bundle 项目 (`*.Bundle.wixproj`) |
|---|---|---|
| `Product.wxs` | ✅ 包含（SDK 自动） | ❌ 排除（DefaultItemExcludes） |
| `Bundle.wxs` | ❌ 排除 | ✅ 包含（SDK 自动） |
| `*.wxl`（本地化） | ✅ 用 `<WixLocalization>` 显式声明 | ❌ 全部排除（避免重复本地化 ID） |
| `banner.png / dialog_bg.png / logo.* / license.rtf` | 自动引用（Product.wxs 的 `WixVariable`） | Bundle.wxs 引用 logo.ico + logo_large.png |

### 4.2 MSI 关键点（[Product.wxs](src/PazeE.Installer/Product.wxs)）

- **UpgradeCode**：`8F4E2A91-3B7C-4D9E-8A12-5F6E1C7B2D90`（发布后不可改）
- **UI 向导**：`<ui:WixUI Id="WixUI_InstallDir" />`（Python 式 3 步：许可协议 → 安装路径 → 安装 → 完成）
- **目录**：`C:\Program Files\PazeE\0.1.0-alpha\`（`StandardDirectory: ProgramFiles64Folder → PazeEFolder → INSTALLFOLDER`）
- **PATH**：`PathEnvironmentComponent` 把 INSTALLFOLDER 追加到系统 PATH；卸载时移除
- **注册表**：`HKLM\SOFTWARE\PazeE\0.1.0-alpha\InstallPath` 和 `Version`
- **heat 自动收割**：`<HarvestDirectory>` 把 `publish/0.1.0-alpha/bin/` 全部文件打包为 `PazeEBinFiles` ComponentGroup
- **品牌化位图**：
  - `WixUIBannerBmp`=banner.png (493×58) — 除"许可协议/目标文件夹"等 Banner，**左侧必须绝对留白**。当前 Logo 在右下角 (435,3, 52×52) 不挡文字。
  - `WixUIDialogBmp`=dialog_bg.png (493×312) — 仅用于 WelcomeDlg + ExitDlg。左侧 0-180px 为深色品牌区（含大 Logo 和品牌文字），右侧 180-493px 为纯白 + 极淡蓝渐变（WixUI 在此处渲染"欢迎使用/安装向导已完成"）。
- **许可协议**：`license.rtf`（GPL3 全文，Unicode RTF）
- **ARP**（添加/删除程序）：logo.ico 作图标；NOREPAIR=1（不显示修复按钮）
- **默认 UI 语言**：`Property UILanguage=zh-cn`

### 4.3 Bundle EXE 关键点（[Bundle.wxs](src/PazeE.Installer/Bundle.wxs)）

- **UpgradeCode**：`D7E3B1F4-2A5C-4E89-B16F-3C8D9E2A7B41`（区别于 MSI 的 UpgradeCode）
- **Bootstrapper Application**：`<bal:WixInternalUIBootstrapperApplication LogoFile="logo_large.png" />`
  - 不同于常规的 WixStandardBootstrapperApplication（它有自己的对话框）
  - WixInternalUIBootstrapperApplication 直接把流程交给内嵌 MSI 的 WixUI_InstallDir 向导
  - 用户双击 .exe → 直接看到品牌化的 MSI 向导，完全感知不到 Bundle 层
- **Chain**：一个 `<MsiPackage SourceFile="$(var.MsiTargetPath)" ForcePerMachine="yes" Vital="yes" />`
- **MsiTargetPath**（Bundle.wixproj 的 DefineConstants）：`$(MSBuildProjectDirectory)\obj\Release\zh-cn\PazeE-0.1.0-alpha-win-x64.msi` —— 指向 MSI 项目构建产物中的**默认语言（zh-cn）** MSI

### 4.4 多语言支持

三个本地化文件（zh-CN / en-US / zh-TW），每个文件 32 条字符串：

| 类别 | 示例 ID |
|---|---|
| 产品信息 | PazeEProductName / PazeEPublisher / ARPComments / ARPContact |
| 欢迎/完成页 | WelcomeTitle / WelcomeText1 / WelcomeText2 / ExitText |
| 许可协议 | LicenseTitle / LicenseText / LicenseAccept / LicenseDecline |
| 路径选择 | InstallDirTitle / InstallDirLabel / InstallDirDesc / InstallDirBrowse / VolatileDir / OutOfDiskSpace |
| 安装进度 | ProgressTitle / InstallingText |
| 错误/升级 | DowngradeError / ErrorTitle |
| 其他 | BinariesTitle / CancelConfirm / DiskCostTitle / OutOfNoRbDiskSpace / VerifyReadyDlgTitle / Back / Next / Install / Cancel / Print / Finish |

**工作原理**：
1. Msi.wixproj 配置 `<Cultures>zh-cn;en-us;zh-tw</Cultures>` + `<DefaultCulture>zh-cn</DefaultCulture>`
2. `<WixLocalization Include="en-US.wxl" Culture="en-us" />` 等把 .wxl 对应到 Culture
3. `Product.wxs` 中所有面向用户的文本用 `!(loc.XXX)` 引用
4. 构建时每个 culture 产出一个变换后的 MSI
5. 默认 zh-cn 的 MSI 复制到 publish/installer 目录；另外两个 culture 留在 obj 中（可按需复制）
6. Bundle 内嵌 zh-cn MSI；MSI 自身的 transforms 支持自动按系统语言切换

**XML 实体注意**：.wxl 是 XML 文件。中文全角引号 `"` / `"` 是非法 XML 属性字符——用 `&#x201C;` 和 `&#x201D;` 替代。任何 `<` 用 `&lt;`，`&` 用 `&amp;`。

### 4.5 版本号/路径硬编码清单

以下位置写死了 `0.1.0-alpha` / `0.1.0`，版本升级时需全部同步更新：

| 文件 | 字段 | 需更新 |
|---|---|---|
| [PazeE.Compiler.csproj](src/PazeE.Compiler/PazeE.Compiler.csproj) | `<VersionPrefix>` | ✅ `0.1.0` |
| [PazeE.Installer.Msi.wixproj](src/PazeE.Installer/PazeE.Installer.Msi.wixproj) | `<OutputName>`, `<PublishBinDir>`, `<DefineConstants>` | ✅ 三处 `0.1.0-alpha` / `0.1.0` |
| [PazeE.Installer.Bundle.wixproj](src/PazeE.Installer/PazeE.Installer.Bundle.wixproj) | `<OutputName>`, `<OutputPath>`, `<DefineConstants>` | ✅ 三处 `0.1.0-alpha` / `0.1.0` |
| [Product.wxs](src/PazeE.Installer/Product.wxs) | `Directory Name="0.1.0-alpha"`, `RegistryKey Key="...\0.1.0-alpha"` | ✅ 两处 |

---

## 5. 常见问题速查

| # | 现象 | 根因 | 解决 |
|---|---|---|---|
| 1 | `WIX0005: Unexpected child element 'WixLocalization'` | `WixLocalization` 不能放 Product.wxs 根节点 | .wxl 文件用 Msi.wixproj 的 `<WixLocalization Include="x.wxl" Culture="..." />` 配置 |
| 2 | `WIX0199: Incorrect namespace for WixLocalization` | .wxl 用了 wxs 命名空间 | .wxl 根命名空间是 `http://wixtoolset.org/schemas/v4/wxl`（不是 wxs） |
| 3 | `WIX0400: String element contains inner text (obsolete)` | WiX v4+ 要求用 Value 属性 | .wxl 中用 `<String Id="X" Value="..." />`，不要 `<String Id="X">...</String>` |
| 4 | XML 解析错误：`'下一步' is an unexpected token` | 中文全角引号破坏 XML 属性 | 用 `&#x201C;`（左双引）和 `&#x201D;`（右双引）代替 |
| 5 | `MSB3030: 无法复制文件 ... .wixpdb` | 多语言构建后 .pdb 在 culture 子目录 | PublishMsi 目标从 `$(OutputPath)$(DefaultCulture)\*.msi` 复制 |
| 6 | `WIX0100: Localization identifier duplicated` | Bundle 也处理了 .wxl | 在 Bundle.wixproj 加 `<DefaultItemExcludes>$(DefaultItemExcludes);Product.wxs;*.wxl</DefaultItemExcludes>` |
| 7 | banner/dialog 位图上的文字与 WixUI 重叠，或 Logo 挡住标题 | Banner 位图左侧放了 Logo/文字，WixUI 在同一位置渲染标题 | Banner 左侧 1-420px 必须纯白；Logo 放右下角（如 (435,3, 52x52)） |
| 8 | 安装 EXE 文件被锁，dotnet build 复制失败（MSB3026 重试，最后 MSB3027） | 之前启动的安装器还在运行 | `Stop-Process -Id 17380 -Force`（从错误信息获取 PID），再手动 Copy-Item |
| 9 | `git push` 失败：`Connection was reset` / `Could not connect to github.com port 443` | 大陆直连 github.com:443 不通 | 用系统代理：`git config set http.proxy "http://127.0.0.1:51081"`；api.github.com 通常可达 |
| 10 | dotnet build Bundle: heat 报错找不到 `publish/bin` 下的文件 | 编译器还没 publish 到指定目录 | 先完成阶段 1：`dotnet publish src/PazeE.Compiler -p:BuildChannel=alpha -r win-x64 --self-contained` |
| 11 | pull --rebase 报 `Committer identity unknown` | `~/.gitconfig` 无 user.name/email | 用本地配置：`git config set --local user.name "JGZ_YES" ; git config set --local user.email "luoriguodu@qq.com"` |
| 12 | 安装后 `paze --help` 命令找不到 | PATH 尚未刷新 | 安装器已添加到系统 PATH；需重新打开终端（Explorer/CMD/PowerShell 只在启动时读取 PATH） |

---

## 6. 后续可做事项（Roadmap 建议）

- [ ] **版本升级路径**：目前 UpgradeCode 已就位，但尚未验证跨版本升级场景（0.1.0 → 0.1.1）。需测 MajorUpgrade 行为。
- [ ] **代码签名**：MSI/EXE 目前未签名，SmartScreen 会警告。可通过 EV 代码证书 + signtool 签名。签名步骤加在 Bundle.wixproj 构建后。
- [ ] **WiX `WixUI_Minimal` vs `WixUI_InstallDir`**：如果想把安装路径选择改成"高级选项"，可切回 WixUI_Minimal 或自定义 DialogSet。
- [ ] **多语言 Bundle**：目前 Bundle 只嵌 zh-cn 的 MSI。可考虑一个 Bundle 内嵌三语 MSI，或 Build 三个语言版本的 EXE。
- [ ] **ARM64 安装包**：编译器已支持 AArch64 代码生成，但 publish + installer 只有 win-x64。需要 `win-arm64` runtime identifier 和对应 MSI/Bundle 产物。
- [ ] **卸载残留**：PATH 组件为 Permanent="no"、注册表无永久标记，卸载路径应干净；但需实测卸载后是否移除 `HKLM\SOFTWARE\PazeE\`（当前 Component KeyPath 是 RegistryKey，卸载应自动移除）。
- [ ] **编译器测试**：tests/ 目录存在但尚未充实；可加入 lex/parse/sema/codegen 的端到端回归用例。
- [ ] **AGENT.MD 本身的维护**：每次结构大改（特别是安装器、csproj、版本号方案）后记得更新本文档。

---

## 7. 变更日志（接手参考）

| 时间 | 事件 | 提交 |
|---|---|---|
| 2026-08-02 初 | 初始提交 + Alpha-0.1 源代码 | 83fe7bf |
| 2026-08-02 | 加入安装器 GPL3 + WixUI_Mondo + ARP 信息 | 844b4a4 |
| 2026-08-02 | 品牌化位图（banner/dialog_bg） | 4d46b61 |
| 2026-08-02 | 切 WixUI_InstallDir（Python 式 3 步） | b1c7210 |
| 2026-08-02 | 切 WixInternalUIBootstrapperApplication（.exe 直出 MSI 向导） | 08cef4f |
| 2026-08-02 | 三语本地化（zh-CN/en-us/zh-TW + .wxl + Cultures） | 2ed6aa2 |
| 2026-08-02 | 修复位图内嵌文字与 WixUI 标题重叠 | d5f9ecd → f4cf6f8 |
| 2026-08-02 | 用新 500x500 logo.png 重建所有衍生文件 + installer | 41c19b0 → 9fac7cb |

---

**接手前必读**：在任何 `dotnet build installer` 之前，先确认 `publish/0.1.0-alpha/bin/` 下已有 paze.exe 和自包含运行时。若没有，必先运行阶段 1 的 `dotnet publish`。logo.png 更新后必须重新生成四个衍生文件（logo.ico / logo_large.png / banner.png / dialog_bg.png）再构建安装器。
