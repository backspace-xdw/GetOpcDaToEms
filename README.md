# OPC DA → EMS 数据转发工具

从 OPC DA 服务器自动读取数据，转发到 EMS Plus 系统。

## 使用方法

1. 编辑 `OpcEmsConfig.ini`，填写 OPC 服务器地址
2. 双击 `Run.cmd` 启动

## 配置文件

```ini
[Server]
ProgId=Hollysys.HOLLiASiComm.1
HostName=192.168.1.100

[Polling]
IntervalMs=1000
ReadMode=SyncCache
```

`[Points]` 留空则自动浏览 OPC 服务器全部点位。

## 运行环境

- Windows 7 32位 / Windows 10
- .NET Framework 4.5.2+
- OPC Core Components (x86)
- EosDapi.dll (EMS Plus)

## 构建

```
Build.cmd
```
