# Third-Party Notices

Orkeon Studio is released under the [MIT License](LICENSE.md). The applications and the
archives built from this repository bundle the third-party components below, each under its
own license. Versions are the ones pinned in `Directory.Packages.props` and resolved
transitively by the restore; the license of each package is the one its NuGet metadata declares.

The set is the NuGet packages the `src/` projects reference, directly or transitively. Build-time
and test-only packages (the `Orkeon.Compliance.Vfs` analyzer, the Roslyn compiler toolset, xUnit,
the test SDK) are not shipped and are not listed. The .NET runtime is covered by its own
license, distributed with the SDK or runtime that runs Studio.

Orkeon Studio drives the `orkeon` CLI, which is installed separately and carries its own
notices: see https://github.com/Orkeon/orkeon/blob/main/THIRD-PARTY-NOTICES.md.

| Package | License | Project |
|---|---|---|
| Apache.Arrow | Apache-2.0 | https://arrow.apache.org/ |
| Apache.Arrow.Scalars | Apache-2.0 | https://arrow.apache.org/ |
| AWSSDK.Core | Apache-2.0 | https://github.com/aws/aws-sdk-net/ |
| AWSSDK.SecretsManager | Apache-2.0 | https://github.com/aws/aws-sdk-net/ |
| Azure.Core | MIT | https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.54.0/sdk/core/Azure.Core/README.md |
| Azure.Identity | MIT | https://github.com/Azure/azure-sdk-for-net/blob/Azure.Identity_1.21.0/sdk/identity/Azure.Identity/README.md |
| Azure.Security.KeyVault.Secrets | MIT | https://github.com/Azure/azure-sdk-for-net/blob/Azure.Security.KeyVault.Secrets_4.11.2/sdk/keyvault/Azure.Security.KeyVault.Secrets/README.md |
| ColorHelper | MIT | https://github.com/iamartyom/ColorHelper |
| CsvHelper | MS-PL OR Apache-2.0 | https://joshclose.github.io/CsvHelper/ |
| HtmlAgilityPack | MIT | http://html-agility-pack.net/ |
| JetBrains.Annotations | MIT | https://www.jetbrains.com/help/resharper/Code_Analysis__Code_Annotations.html |
| Markdig | BSD-2-Clause | https://xoofx.github.io/markdig |
| Microsoft.Bcl.AsyncInterfaces | MIT | https://dot.net/ |
| Microsoft.Bcl.Cryptography | MIT | https://dot.net/ |
| Microsoft.CodeAnalysis.Common | MIT | https://github.com/dotnet/roslyn |
| Microsoft.CodeAnalysis.CSharp | MIT | https://github.com/dotnet/roslyn |
| Microsoft.CodeAnalysis.CSharp.Scripting | MIT | https://github.com/dotnet/roslyn |
| Microsoft.CodeAnalysis.Scripting.Common | MIT | https://github.com/dotnet/roslyn |
| Microsoft.Data.Sqlite | MIT | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Data.Sqlite.Core | MIT | https://docs.microsoft.com/dotnet/standard/data/sqlite/ |
| Microsoft.Extensions.AI | MIT | https://dot.net/ |
| Microsoft.Extensions.AI.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.AI.OpenAI | MIT | https://dot.net/ |
| Microsoft.Extensions.Caching.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.Binder | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.EnvironmentVariables | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.FileExtensions | MIT | https://dot.net/ |
| Microsoft.Extensions.Configuration.Json | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection | MIT | https://dot.net/ |
| Microsoft.Extensions.DependencyInjection.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.Diagnostics | MIT | https://dot.net/ |
| Microsoft.Extensions.Diagnostics.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.Diagnostics.HealthChecks | MIT | https://asp.net/ |
| Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions | MIT | https://asp.net/ |
| Microsoft.Extensions.FileProviders.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.FileProviders.Physical | MIT | https://dot.net/ |
| Microsoft.Extensions.FileSystemGlobbing | MIT | https://dot.net/ |
| Microsoft.Extensions.Hosting.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.Http | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Abstractions | MIT | https://dot.net/ |
| Microsoft.Extensions.Logging.Configuration | MIT | https://dot.net/ |
| Microsoft.Extensions.Options | MIT | https://dot.net/ |
| Microsoft.Extensions.Options.ConfigurationExtensions | MIT | https://dot.net/ |
| Microsoft.Extensions.Primitives | MIT | https://dot.net/ |
| Microsoft.Identity.Client | MIT | https://go.microsoft.com/fwlink/?linkid=844761 |
| Microsoft.Identity.Client.Extensions.Msal | MIT | https://go.microsoft.com/fwlink/?linkid=844761 |
| Microsoft.IdentityModel.Abstractions | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.JsonWebTokens | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Logging | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Protocols | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Microsoft.IdentityModel.Tokens | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| Npgsql | PostgreSQL | https://github.com/npgsql/npgsql |
| Onigwrap | MIT | https://github.com/aikawayataro/Onigwrap |
| OpenAI | MIT | https://github.com/openai/openai-dotnet/tree/OpenAI_2.14.0 |
| OpenTelemetry | Apache-2.0 | https://opentelemetry.io/ |
| OpenTelemetry.Api | Apache-2.0 | https://opentelemetry.io/ |
| OpenTelemetry.Api.ProviderBuilderExtensions | Apache-2.0 | https://opentelemetry.io/ |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | Apache-2.0 | https://opentelemetry.io/ |
| OpenTelemetry.Extensions.Hosting | Apache-2.0 | https://opentelemetry.io/ |
| OpenTelemetry.Instrumentation.Http | Apache-2.0 | https://opentelemetry.io/ |
| OpenTelemetry.Instrumentation.Runtime | Apache-2.0 | https://opentelemetry.io/ |
| Orkeon | MIT | https://github.com/Orkeon/orkeon |
| Orkeon.Cli.Abstractions | MIT | https://github.com/Orkeon/orkeon |
| Orkeon.Cli.TerminalGui | MIT | https://github.com/Orkeon/orkeon |
| Orkeon.Constants.Cli | MIT | https://github.com/Orkeon/orkeon |
| PdfPig | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| Polly | BSD-3-Clause | https://github.com/App-vNext/Polly |
| Polly.Core | BSD-3-Clause | https://github.com/App-vNext/Polly |
| RESPite | MIT | https://seredis.dev/ |
| SQLitePCLRaw.bundle_e_sqlite3 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.core | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.lib.e_sqlite3 | Apache-2.0 (SQLite itself is in the public domain) | https://github.com/ericsink/SQLitePCL.raw |
| SQLitePCLRaw.provider.e_sqlite3 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| StackExchange.Redis | MIT | https://seredis.dev/ |
| System.ClientModel | MIT | https://github.com/Azure/azure-sdk-for-net/blob/System.ClientModel_1.15.0/sdk/core/System.ClientModel/README.md |
| System.IdentityModel.Tokens.Jwt | MIT | https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet |
| System.IO.Abstractions | MIT | https://github.com/TestableIO/System.IO.Abstractions |
| System.IO.Hashing | MIT | https://dot.net/ |
| System.Memory.Data | MIT | https://dot.net/ |
| System.Numerics.Tensors | MIT | https://dot.net/ |
| System.Security.Cryptography.ProtectedData | MIT | https://dot.net/ |
| System.Threading.RateLimiting | MIT | https://dot.net/ |
| Terminal.Gui | MIT | https://github.com/tui-cs/Terminal.Gui |
| TestableIO.System.IO.Abstractions | MIT | https://github.com/TestableIO/System.IO.Abstractions |
| TestableIO.System.IO.Abstractions.Wrappers | MIT | https://github.com/TestableIO/System.IO.Abstractions |
| Testably.Abstractions.FileSystem.Interface | MIT | https://github.com/Testably/Testably.Abstractions |
| TextMateSharp | MIT | https://github.com/danipen/TextMateSharp |
| TextMateSharp.Grammars | MIT | https://github.com/danipen/TextMateSharp |
| TreeSitter.DotNet | MIT | https://github.com/mariusgreuel/tree-sitter-dotnet-bindings |
| Ulid | MIT | https://github.com/Cysharp/Ulid |
| Wcwidth | MIT | https://github.com/spectreconsole/wcwidth |
| YamlDotNet | MIT | https://github.com/aaubry/YamlDotNet/wiki |

Regenerate this list when a package reference or a pinned version changes: derive it from the
`obj/project.assets.json` of the `src/` projects after a restore.
