// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.JSInterop;
using Bicep.Core.Diagnostics;
using Bicep.Core.Text;
using Bicep.Core.Emit;
using Bicep.Core.Semantics;
using Bicep.Wasm.LanguageHelpers;
using System.Linq;
using Bicep.Core.TypeSystem.Az;
using Bicep.Core.FileSystem;
using Bicep.Core.Workspaces;
using Bicep.Core.Extensions;
using Bicep.Decompiler;
using Bicep.Core.Registry;
using Bicep.Core.Semantics.Namespaces;
using Bicep.Core.Features;
using Bicep.Core.Configuration;
using IOFileSystem = System.IO.Abstractions.FileSystem;
using Bicep.Core.Analyzers.Linter;

namespace Bicep.Wasm
{
    public class Interop
    {
        private static readonly IFeatureProvider features = new FeatureProvider();

        private static readonly INamespaceProvider namespaceProvider = new DefaultNamespaceProvider(new AzResourceTypeLoader(), features);

        private readonly IJSRuntime jsRuntime;

        public Interop(IJSRuntime jsRuntime)
        {
            this.jsRuntime = jsRuntime;
        }

        [JSInvokable]
        public object CompileAndEmitDiagnostics(string content)
        {
            var (output, diagnostics) = CompileInternal(content);

            return new
            {
                template = output,
                diagnostics = diagnostics,
            };
        }

        public record DecompileResult(string? bicepFile, string? error);

        [JSInvokable]
        public DecompileResult Decompile(string jsonContent)
        {
            var jsonUri = new Uri("inmemory:///main.json");

            var fileResolver = new InMemoryFileResolver(new Dictionary<Uri, string>
            {
                [jsonUri] = jsonContent,
            });

            try
            {
                var bicepUri = PathHelper.ChangeToBicepExtension(jsonUri);
                var decompiler = new TemplateDecompiler(features, namespaceProvider, fileResolver, new EmptyModuleRegistryProvider(), new ConfigurationManager(new IOFileSystem()));
                var (entrypointUri, filesToSave) = decompiler.DecompileFileWithModules(jsonUri, bicepUri);

                return new DecompileResult(filesToSave[entrypointUri], null);
            }
            catch (Exception exception)
            {
                return new DecompileResult(null, exception.Message);
            }
        }

        [JSInvokable]
        public object GetSemanticTokensLegend()
        {
            var tokenTypes = Enum.GetValues(typeof(SemanticTokenType)).Cast<SemanticTokenType>();
            var tokenStrings = tokenTypes.OrderBy(t => (int)t).Select(t => t.ToString().ToLowerInvariant());

            return new
            {
                tokenModifiers = new string[] { },
                tokenTypes = tokenStrings.ToArray(),
            };
        }

        [JSInvokable]
        public object GetSemanticTokens(string content)
        {
            var compilation = GetCompilation(content);
            var tokens = SemanticTokenVisitor.BuildSemanticTokens(compilation.SourceFileGrouping.EntryPoint);

            var data = new List<int>();
            SemanticToken? prevToken = null;
            foreach (var token in tokens)
            {
                if (prevToken == null)
                {
                    data.Add(token.Line);
                    data.Add(token.Character);
                    data.Add(token.Length);
                }
                else if (prevToken.Line != token.Line)
                {
                    data.Add(token.Line - prevToken.Line);
                    data.Add(token.Character);
                    data.Add(token.Length);
                }
                else
                {
                    data.Add(0);
                    data.Add(token.Character - prevToken.Character);
                    data.Add(token.Length);
                }

                data.Add((int)token.TokenType);
                data.Add(0);

                prevToken = token;
            }

            return new
            {
                data = data.ToArray(),
            };
        }

        private static (string, IEnumerable<object>) CompileInternal(string content)
        {
            try
            {
                var compilation = GetCompilation(content);
                var lineStarts = compilation.SourceFileGrouping.EntryPoint.LineStarts;
                var emitterSettings = new EmitterSettings(features);
                var emitter = new TemplateEmitter(compilation.GetEntrypointSemanticModel(), emitterSettings);

                // memory stream is not ideal for frequent large allocations
                using var stream = new MemoryStream();
                var emitResult = emitter.Emit(stream);

                if (emitResult.Status != EmitStatus.Failed)
                {
                    // compilation was successful or had warnings - return the compiled template
                    stream.Position = 0;
                    return (ReadStreamToEnd(stream), emitResult.Diagnostics.Select(d => ToMonacoDiagnostic(d, lineStarts)));
                }

                // compilation failed
                return ("Compilation failed!", emitResult.Diagnostics.Select(d => ToMonacoDiagnostic(d, lineStarts)));
            }
            catch (Exception exception)
            {
                return (exception.ToString(), Enumerable.Empty<object>());
            }
        }

        private static Compilation GetCompilation(string fileContents)
        {
            var fileUri = new Uri("inmemory:///main.bicep");
            var workspace = new Workspace();
            var sourceFile = SourceFileFactory.CreateSourceFile(fileUri, fileContents);
            workspace.UpsertSourceFile(sourceFile);

            var fileResolver = new FileResolver();
            var dispatcher = new ModuleDispatcher(new EmptyModuleRegistryProvider());
            var configurationManager = new ConfigurationManager(new IOFileSystem());
            var configuration = configurationManager.GetBuiltInConfiguration().WithAllAnalyzersDisabled();
            var sourceFileGrouping = SourceFileGroupingBuilder.Build(fileResolver, dispatcher, workspace, fileUri, configuration);

            return new Compilation(features, namespaceProvider, sourceFileGrouping, configuration, new LinterAnalyzer(configuration));
        }

        private static string ReadStreamToEnd(Stream stream)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static object ToMonacoDiagnostic(IDiagnostic diagnostic, IReadOnlyList<int> lineStarts)
        {
            var (startLine, startChar) = TextCoordinateConverter.GetPosition(lineStarts, diagnostic.Span.Position);
            var (endLine, endChar) = TextCoordinateConverter.GetPosition(lineStarts, diagnostic.GetEndPosition());

            return new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                severity = ToMonacoSeverity(diagnostic.Level),
                startLineNumber = startLine + 1,
                startColumn = startChar + 1,
                endLineNumber = endLine + 1,
                endColumn = endChar + 1,
            };
        }

        private static int ToMonacoSeverity(DiagnosticLevel level)
            => level switch
            {
                DiagnosticLevel.Info => 2,
                DiagnosticLevel.Warning => 4,
                DiagnosticLevel.Error => 8,
                _ => throw new ArgumentException($"Unrecognized level {level}"),
            };
    }
}
