# 代码签名——怎么开启

## 现状

[build-and-release.yml](../.github/workflows/build-and-release.yml) 里已经接好了签名的架子，
默认走 **SignPath Foundation 的开源项目免费签名服务**。**申请已提交，审批中**
（signpath.org 提交于 2026-08-10，回复通常几天内到邮箱）。批下来之前，
签名相关的几步会因为 `SIGNPATH_API_TOKEN` 这个 secret 是空的而被跳过，
构建照常产出未签名的 exe 和安装包，不影响现有发布流程。

为什么选 SignPath：免费（针对开源项目）、不需要买证书或硬件加密狗、
跟 GitHub Actions 原生集成。代价是每次发版都要经过它的签名请求流程
（可能要等审批），而且是"专门给这个项目签"，不是你自己名下能到处用的证书。

## 开启步骤

1. **申请**（✅ 已完成，见上面「现状」）：去 <https://signpath.org/>（SignPath
   Foundation，负责开源免费签名的独立主体，跟卖企业版的 signpath.io 是两码事），
   点导航栏的 "Apply" 提交申请。审批通常几天内有结果，邮件通知。

2. **建项目**：审批通过后登录 SignPath，新建一个 Project（比如叫
   `CtrlTranslator`），它会自动带两个默认的 Signing Policy：
   `test-signing`（测试用，审批松）和 `release-signing`（正式发布用，审批严）。
   工作流里已经按 tag 是否带 `rc`/`beta`/`alpha`/`preview` 自动在这两者之间切换，
   不用改代码。

3. **拿凭证**：在 SignPath 后台生成一个 API Token，记下 Organization ID
   和 Project 的 slug（项目设置页能看到）。

4. **填进 GitHub**：仓库 Settings → Secrets and variables → Actions：

   | 类型 | 名字 | 值 |
   |---|---|---|
   | Secret | `SIGNPATH_API_TOKEN` | 第 3 步生成的 Token |
   | Variable | `SIGNPATH_ORGANIZATION_ID` | 你的 Organization ID |
   | Variable | `SIGNPATH_PROJECT_SLUG` | 第 2 步建的项目 slug |

5. **完事**：不用改工作流文件。下次推正式 tag（`git tag v1.x.x && git push
   origin v1.x.x`），Actions 里会多出「签名 exe」「签名安装包」两步，
   Release 附件就是签过名的了。

## 验证签好了没

下载 Release 里的 exe，右键 → 属性 → 数字签名，应该能看到一个有效的签名条目。
或者命令行：

```powershell
Get-AuthenticodeSignature .\CtrlTranslator.exe | Select-Object Status, SignerCertificate
```

`Status` 是 `Valid` 就对了。

## 关于 SmartScreen 信誉

签上名只是第一步——Authenticode 签名解决的是"这个文件没被篡改、发布者身份可核实"，
不等于 Windows SmartScreen 立刻放行。普通签名证书的 SmartScreen 信誉是按**下载量**
慢慢积累的，前面一段时间可能还是会看到"Windows 已保护你的电脑"这类提示（但已经
比完全未签名好很多，用户可以点"更多信息 → 仍要运行"）。如果后面愿意加预算，
换成 EV 证书或 Azure Trusted Signing 能让这个信誉即时生效，见下面。

## 如果最后没走 SignPath，换成别的方案

工作流里"代码签名"那一整段（`上传未签名 exe` 到 `用签名后的安装包替换未签名版本`
这几步）要整体换掉，其余部分（打包、算哈希、建 Release）不用动：

- **Azure Trusted Signing**（约 $10/月，个人开发者可申请，信誉积累比普通证书快）：
  改用 `azure/trusted-signing-action` 这个官方 Action，需要 Azure 租户 ID、
  客户端 ID/密钥（或 OIDC 联合身份）、Trusted Signing 账户名和证书配置文件名，
  都以 secrets/variables 形式配置。
- **自购 OV/EV 证书**：如果证书私钥在云端 HSM（多数正规 CA 现在都要求私钥存在
  硬件里，不再给可导出的 PFX 文件），一般对应厂商会提供自己的签名 CLI 或
  GitHub Action（比如 DigiCert KeyLocker、SSL.com eSigner），照厂商文档接进来，
  同样是"传未签名文件进去、拿签名后的文件出来"的模式，跟现在的架子替换起来不难。

需要哪种，找我说一声，把对应的 Action 和参数接上去就行。
