# 代码签名——现状与将来怎么开启

## 现状：不签名

Release 产物（exe 和安装包）**目前没有代码签名**，短期内也不打算签。

原本走的是 **SignPath Foundation 的开源项目免费签名服务**，2026-08-10 提交申请，
**2026-08-13 收到拒信**。拒绝的理由跟代码质量、项目本身没关系，是"公开信誉门槛"没过——
原话大意是该项目面向的是已经建立起一定公开信任度和知名度的项目，评审时看的是
社区采纳度（star / fork / 贡献者）、外部文章和独立讨论（Reddit、Stack Overflow、
YouTube 等）、机构背书，以及持续活跃的证据。新项目基本都会卡在这一条。

这不是永久拒绝，项目以后有热度了可以重新申请。

## 不签名的实际影响

只有一条：**用户首次运行安装包时会弹一次「Windows 已保护你的电脑」**
（SmartScreen）。点提示框里的「更多信息」→「仍要运行」就能继续，同一个文件之后不再弹。

README 的下载段落里已经写了这句给用户看。杀毒软件误报是另一码事
（全局键盘钩子 + 模拟 Ctrl+C + 读剪贴板的组合特征），签名也不保证解决，
处理办法在 README 的「杀毒软件可能误报」里。

## 工作流现在是什么状态

[build-and-release.yml](../.github/workflows/build-and-release.yml) 里签名的架子还留着，
但四个签名相关的 step 都带着同一个条件：

```yaml
if: startsWith(github.ref, 'refs/tags/v') && env.SIGNPATH_API_TOKEN != ''
```

`SIGNPATH_API_TOKEN` 这个 secret 没配，所以这几步直接跳过，构建照常产出未签名的
exe 和安装包。Release 说明里也会自动带上"这份构建**没有代码签名**"的提示
（见工作流里的 `$signedNote`）。

**不需要为了"关掉签名"去改任何东西**——它本来就是关着的。

## 将来想签的话，三条路

### A. 重新申请 SignPath Foundation（免费）

等项目有了一定 star / 外部讨论之后回 <https://signpath.org/> 重新申请。
批下来之后：

1. 登录 SignPath 新建一个 Project（比如叫 `CtrlTranslator`），它会自动带两个默认的
   Signing Policy：`test-signing`（测试用，审批松）和 `release-signing`（正式发布用，
   审批严）。工作流里已经按 tag 是否带 `rc`/`beta`/`alpha`/`preview` 自动在这两者之间
   切换，不用改代码。
2. 在后台生成 API Token，记下 Organization ID 和 Project 的 slug。
3. 仓库 Settings → Secrets and variables → Actions 填进去：

   | 类型 | 名字 | 值 |
   |---|---|---|
   | Secret | `SIGNPATH_API_TOKEN` | 第 2 步生成的 Token |
   | Variable | `SIGNPATH_ORGANIZATION_ID` | Organization ID |
   | Variable | `SIGNPATH_PROJECT_SLUG` | 第 1 步建的项目 slug |

4. 完事，工作流文件一个字都不用动。下次推正式 tag 就会自动签。

注意 signpath.org（Foundation，负责开源免费签名的独立主体）跟卖企业版的
signpath.io 是两码事，别申请错。

### B. Azure Trusted Signing（约 $10/月）

微软自己的托管签名服务，好处是 SmartScreen 信誉几乎立刻生效，不用靠下载量慢慢攒。
个人开发者可以申请，但**对可验证身份和所在地区有限制，不一定申请得下来**，
掏钱之前先去确认当前的资格要求。

接法：把工作流里"代码签名"那一整段（`上传未签名 exe` 到
`用签名后的安装包替换未签名版本`）换成官方的 `azure/trusted-signing-action`，
需要 Azure 租户 ID、客户端 ID/密钥（或 OIDC 联合身份）、Trusted Signing 账户名
和证书配置文件名，都以 secrets/variables 配置。其余部分（打包、算哈希、建 Release）
不用动。

### C. 自购 OV/EV 证书（一年几百美元）

OV 证书的 SmartScreen 信誉同样要按下载量积累，前期照样弹提示——花了钱问题还在，
性价比最差。EV 立刻有信誉，但更贵，而且多半要硬件 token。

现在多数正规 CA 已经不再提供可导出的 PFX 文件（私钥必须存在硬件或云端 HSM 里），
对应厂商一般会提供自己的签名 CLI 或 GitHub Action（DigiCert KeyLocker、
SSL.com eSigner 等），照厂商文档接进来，同样是"传未签名文件进去、拿签名后的文件
出来"的模式，跟现在的架子替换起来不难。

## 签好之后怎么验证

下载 Release 里的 exe，右键 → 属性 → 数字签名，应该能看到一个有效的签名条目。
或者命令行：

```powershell
Get-AuthenticodeSignature .\CtrlTranslator.exe | Select-Object Status, SignerCertificate
```

`Status` 是 `Valid` 就对了。

要注意的是：Authenticode 签名解决的是"这个文件没被篡改、发布者身份可核实"，
**不等于 SmartScreen 立刻放行**。走 A 或 C 的话，前面一段时间可能还是会看到
"Windows 已保护你的电脑"，只是比完全未签名好一些。只有 B（Azure Trusted Signing）
和 EV 证书能让信誉即时生效。
