// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Implementation.Diagnostics;
using Microsoft.CodeAnalysis.Editor.Shared.Preview;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Text;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.UnitTests.Preview
{
    public class PreviewWorkspaceTests
    {
        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewCreationDefault()
        {
            using (var previewWorkspace = new PreviewWorkspace())
            {
                Assert.NotNull(previewWorkspace.CurrentSolution);
            }
        }

        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewCreationWithExplicitHostServices()
        {
            var assembly = typeof(ISolutionCrawlerRegistrationService).Assembly;
            using (var previewWorkspace = new PreviewWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies.Concat(assembly))))
            {
                Assert.NotNull(previewWorkspace.CurrentSolution);
            }
        }

        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewCreationWithSolution()
        {
            using (var custom = new AdhocWorkspace())
            using (var previewWorkspace = new PreviewWorkspace(custom.CurrentSolution))
            {
                Assert.NotNull(previewWorkspace.CurrentSolution);
            }
        }

        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewAddRemoveProject()
        {
            using (var previewWorkspace = new PreviewWorkspace())
            {
                var solution = previewWorkspace.CurrentSolution;
                var project = solution.AddProject("project", "project.dll", LanguageNames.CSharp);
                Assert.True(previewWorkspace.TryApplyChanges(project.Solution));

                var newSolution = previewWorkspace.CurrentSolution.RemoveProject(project.Id);
                Assert.True(previewWorkspace.TryApplyChanges(newSolution));

                Assert.Equal(0, previewWorkspace.CurrentSolution.ProjectIds.Count);
            }
        }

        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewProjectChanges()
        {
            using (var previewWorkspace = new PreviewWorkspace())
            {
                var solution = previewWorkspace.CurrentSolution;
                var project = solution.AddProject("project", "project.dll", LanguageNames.CSharp);
                Assert.True(previewWorkspace.TryApplyChanges(project.Solution));

                var addedSolution = previewWorkspace.CurrentSolution.Projects.First()
                                                    .AddMetadataReference(TestReferences.NetFx.v4_0_30319.mscorlib)
                                                    .AddDocument("document", "").Project.Solution;
                Assert.True(previewWorkspace.TryApplyChanges(addedSolution));
                Assert.Equal(1, previewWorkspace.CurrentSolution.Projects.First().MetadataReferences.Count);
                Assert.Equal(1, previewWorkspace.CurrentSolution.Projects.First().DocumentIds.Count);

                var text = "class C {}";
                var changedSolution = previewWorkspace.CurrentSolution.Projects.First().Documents.First().WithText(SourceText.From(text)).Project.Solution;
                Assert.True(previewWorkspace.TryApplyChanges(changedSolution));
                Assert.Equal(previewWorkspace.CurrentSolution.Projects.First().Documents.First().GetTextAsync().Result.ToString(), text);

                var removedSolution = previewWorkspace.CurrentSolution.Projects.First()
                                                    .RemoveMetadataReference(previewWorkspace.CurrentSolution.Projects.First().MetadataReferences[0])
                                                    .RemoveDocument(previewWorkspace.CurrentSolution.Projects.First().DocumentIds[0]).Solution;

                Assert.True(previewWorkspace.TryApplyChanges(removedSolution));
                Assert.Equal(0, previewWorkspace.CurrentSolution.Projects.First().MetadataReferences.Count);
                Assert.Equal(0, previewWorkspace.CurrentSolution.Projects.First().DocumentIds.Count);
            }
        }

        [WorkItem(923121)]
        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewOpenCloseFile()
        {
            using (var previewWorkspace = new PreviewWorkspace())
            {
                var solution = previewWorkspace.CurrentSolution;
                var project = solution.AddProject("project", "project.dll", LanguageNames.CSharp);
                var document = project.AddDocument("document", "");

                Assert.True(previewWorkspace.TryApplyChanges(document.Project.Solution));

                previewWorkspace.OpenDocument(document.Id);
                Assert.Equal(1, previewWorkspace.GetOpenDocumentIds().Count());
                Assert.True(previewWorkspace.IsDocumentOpen(document.Id));

                previewWorkspace.CloseDocument(document.Id);
                Assert.Equal(0, previewWorkspace.GetOpenDocumentIds().Count());
                Assert.False(previewWorkspace.IsDocumentOpen(document.Id));
            }
        }

        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewServices()
        {
            using (var previewWorkspace = new PreviewWorkspace(MefV1HostServices.Create(TestExportProvider.ExportProviderWithCSharpAndVisualBasic.AsExportProvider())))
            {
                var workcoordinatorService = previewWorkspace.Services.GetService<ISolutionCrawlerRegistrationService>();
                Assert.True(workcoordinatorService is PreviewSolutionCrawlerRegistrationService);

                var persistentService = previewWorkspace.Services.GetService<IPersistentStorageService>();
                Assert.NotNull(persistentService);

                var storage = persistentService.GetStorage(previewWorkspace.CurrentSolution);
                Assert.True(storage is NoOpPersistentStorage);
            }
        }

        [WorkItem(923196)]
        [Fact, Trait(Traits.Editor, Traits.Editors.Preview)]
        public void TestPreviewDiagnostic()
        {
            var diagnosticService = TestExportProvider.ExportProviderWithCSharpAndVisualBasic.GetExportedValue<IDiagnosticAnalyzerService>() as IDiagnosticUpdateSource;

            var taskSource = new TaskCompletionSource<DiagnosticsUpdatedArgs>();
            diagnosticService.DiagnosticsUpdated += (s, a) => taskSource.TrySetResult(a);

            using (var previewWorkspace = new PreviewWorkspace(MefV1HostServices.Create(TestExportProvider.ExportProviderWithCSharpAndVisualBasic.AsExportProvider())))
            {
                var solution = previewWorkspace.CurrentSolution
                                               .AddProject("project", "project.dll", LanguageNames.CSharp)
                                               .AddDocument("document", "class { }")
                                               .Project
                                               .Solution;

                Assert.True(previewWorkspace.TryApplyChanges(solution));

                previewWorkspace.OpenDocument(previewWorkspace.CurrentSolution.Projects.First().DocumentIds[0]);
                previewWorkspace.EnableDiagnostic();

                // wait 20 seconds
                taskSource.Task.Wait(20000);
                if (!taskSource.Task.IsCompleted)
                {
                    // something is wrong
                    FatalError.Report(new System.Exception("not finished after 20 seconds"));
                }

                var args = taskSource.Task.Result;
                Assert.True(args.Diagnostics.Length > 0);
            }
        }

        [Fact]
        public void TestPreviewDiagnosticTagger()
        {
            using (var workspace = CSharpWorkspaceFactory.CreateWorkspaceFromLines("class { }"))
            using (var previewWorkspace = new PreviewWorkspace(workspace.CurrentSolution))
            {
                // set up to listen diagnostic changes so that we can wait until it happens
                var diagnosticService = workspace.ExportProvider.GetExportedValue<IDiagnosticService>() as DiagnosticService;
                var taskSource = new TaskCompletionSource<DiagnosticsUpdatedArgs>();
                diagnosticService.DiagnosticsUpdated += (s, a) => taskSource.TrySetResult(a);

                // preview workspace and owner of the solution now share solution and its underlying text buffer
                var hostDocument = workspace.Projects.First().Documents.First();
                var buffer = hostDocument.GetTextBuffer();

                // enable preview diagnostics
                previewWorkspace.OpenDocument(hostDocument.Id);
                previewWorkspace.EnableDiagnostic();

                var foregroundService = new TestForegroundNotificationService();
                var optionsService = workspace.Services.GetService<IOptionService>();
                var squiggleWaiter = new ErrorSquiggleWaiter();

                // create a tagger for preview workspace
                var taggerSource = new DiagnosticsSquiggleTaggerProvider.TagSource(buffer, foregroundService, diagnosticService, optionsService, squiggleWaiter);

                // wait up to 20 seconds for diagnostic service
                taskSource.Task.Wait(20000);
                if (!taskSource.Task.IsCompleted)
                {
                    // something is wrong
                    FatalError.Report(new System.Exception("not finished after 20 seconds"));
                }

                // wait for tagger
                squiggleWaiter.CreateWaitTask().PumpingWait();

                var snapshot = buffer.CurrentSnapshot;
                var intervalTree = taggerSource.GetTagIntervalTreeForBuffer(buffer);
                var spans = intervalTree.GetIntersectingSpans(new SnapshotSpan(snapshot, 0, snapshot.Length));

                taggerSource.TestOnly_Dispose();

                Assert.Equal(1, spans.Count);
            }
        }

        [Fact]
        public void TestPreviewDiagnosticTaggerInPreviewPane()
        {
            using (var workspace = CSharpWorkspaceFactory.CreateWorkspaceFromLines("class { }"))
            {
                // set up listener to wait until diagnostic finish running
                var diagnosticService = workspace.ExportProvider.GetExportedValue<IDiagnosticService>() as DiagnosticService;

                // no easy way to setup waiter. kind of hacky way to setup waiter
                var source = new CancellationTokenSource();
                var taskSource = new TaskCompletionSource<DiagnosticsUpdatedArgs>();
                diagnosticService.DiagnosticsUpdated += (s, a) =>
                {
                    source.Cancel();

                    source = new CancellationTokenSource();
                    var cancellationToken = source.Token;
                    Task.Delay(2000, cancellationToken).ContinueWith(t => taskSource.TrySetResult(a), TaskContinuationOptions.OnlyOnRanToCompletion);
                };

                var hostDocument = workspace.Projects.First().Documents.First();

                // make a change to remove squiggle
                var oldDocument = workspace.CurrentSolution.GetDocument(hostDocument.Id);
                var oldText = oldDocument.GetTextAsync().Result;

                var newDocument = oldDocument.WithText(oldText.WithChanges(new TextChange(new TextSpan(0, oldText.Length), "class C { }")));

                // create a diff view
                var previewFactoryService = workspace.ExportProvider.GetExportedValue<IPreviewFactoryService>();
                var diffView = previewFactoryService.CreateChangedDocumentPreviewView(oldDocument, newDocument, CancellationToken.None);

                var foregroundService = new TestForegroundNotificationService();
                var optionsService = workspace.Services.GetService<IOptionService>();

                // set up tagger for both buffers
                var leftBuffer = diffView.LeftView.BufferGraph.GetTextBuffers(t => t.ContentType.IsOfType(ContentTypeNames.CSharpContentType)).First();
                var leftWaiter = new ErrorSquiggleWaiter();
                var leftTaggerSource = new DiagnosticsSquiggleTaggerProvider.TagSource(leftBuffer, foregroundService, diagnosticService, optionsService, leftWaiter);

                var rightBuffer = diffView.RightView.BufferGraph.GetTextBuffers(t => t.ContentType.IsOfType(ContentTypeNames.CSharpContentType)).First();
                var rightWaiter = new ErrorSquiggleWaiter();
                var rightTaggerSource = new DiagnosticsSquiggleTaggerProvider.TagSource(rightBuffer, foregroundService, diagnosticService, optionsService, rightWaiter);

                // wait up to 20 seconds for diagnostics
                taskSource.Task.Wait(20000);
                if (!taskSource.Task.IsCompleted)
                {
                    // something is wrong
                    FatalError.Report(new System.Exception("not finished after 20 seconds"));
                }

                // wait taggers
                leftWaiter.CreateWaitTask().PumpingWait();
                rightWaiter.CreateWaitTask().PumpingWait();

                // check left buffer
                var leftSnapshot = leftBuffer.CurrentSnapshot;
                var leftIntervalTree = leftTaggerSource.GetTagIntervalTreeForBuffer(leftBuffer);
                var leftSpans = leftIntervalTree.GetIntersectingSpans(new SnapshotSpan(leftSnapshot, 0, leftSnapshot.Length));

                leftTaggerSource.TestOnly_Dispose();
                Assert.Equal(1, leftSpans.Count);

                // check right buffer
                var rightSnapshot = rightBuffer.CurrentSnapshot;
                var rightIntervalTree = rightTaggerSource.GetTagIntervalTreeForBuffer(rightBuffer);
                var rightSpans = rightIntervalTree.GetIntersectingSpans(new SnapshotSpan(rightSnapshot, 0, rightSnapshot.Length));

                rightTaggerSource.TestOnly_Dispose();
                Assert.Equal(0, rightSpans == null ? 0 : rightSpans.Count);
            }
        }

        private class ErrorSquiggleWaiter : AsynchronousOperationListener { }
    }
}
