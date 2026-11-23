# SeasonTraits

SeasonTraits 是一个 SPT（Single Player Tarkov）mod，为每个游戏档案添加"赛季特质"系统。

## 简介

- **Server 模块**（`SeasonTraits.Server`，net10.0）：提供特质目录、持久化每个档案的特质选择、应用服务端效果（技能等级、加成、邮件物品、跳蚤市场封禁、街头税发放），并通过按档案的 Harmony 补丁应用商人货架与藏身处制造效果。
- **Client 插件**（`SeasonTraits.Client`，net472 BepInEx）：在主菜单显示一次性 uGUI 特质选择面板，把选择提交给服务端，并通过 Harmony 补丁应用游戏内数值/战斗效果；同时把服务端应用的加成/技能镜像到在线会话档案。角色信息 → 技能页里额外提供只读的「特质」子标签，随时查看当前档案已选特质。


## 构建

两个项目分别构建（无解决方案文件）：

```bash
dotnet build -c Release SeasonTraits.Server/SeasonTraits.Server.csproj
dotnet build -c Release SeasonTraits.Client/SeasonTraits.Client.csproj
```

## 安装

- 客户端 DLL → `\BepInEx\plugins\`
- 服务端 → `\SPT_Runtime\user\mods\SeasonTraits\`

## 免责声明

本 mod 仅供个人学习交流使用，与 Battlestate Games 无关，非官方作品。使用者风险自负，作者不对使用本 mod 造成的任何后果负责。

## 许可证

本项目以 **CC0 1.0 Universal**详见 [LICENSE](LICENSE)。
