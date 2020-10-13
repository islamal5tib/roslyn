﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.ConvertToInterpolatedString
{
    /// <summary>
    /// Code refactoring that converts a regular string containing braces to an interpolated string
    /// </summary>
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, LanguageNames.VisualBasic, Name = PredefinedCodeRefactoringProviderNames.ConvertToInterpolatedString), Shared]
    internal sealed class ConvertRegularStringToInterpolatedStringRefactoringProvider : CodeRefactoringProvider
    {
        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public ConvertRegularStringToInterpolatedStringRefactoringProvider()
        {
        }

        public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, _, cancellationToken) = context;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                return;

            var token = root.FindToken(context.Span.Start);
            if (!context.Span.IntersectsWith(token.Span))
                return;

            var syntaxKinds = document.GetRequiredLanguageService<ISyntaxKindsService>();
            if (token.RawKind != syntaxKinds.StringLiteralToken)
                return;

            var literalExpression = token.GetRequiredParent();
            
            // Check the string literal for errors.  This will ensure that we do not try to fixup an incomplete string.
            if (literalExpression.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                return;

            if (!token.Text.Contains("{") && !token.Text.Contains("}"))
                return;

            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();
            if (syntaxFacts is null)
                return;

            // If there is a const keyword, do not offer the refactoring (an interpolated string is not const)
            var declarator = literalExpression.FirstAncestorOrSelf<SyntaxNode>(syntaxFacts.IsVariableDeclarator);
            if (declarator != null)
            {
                var generator = SyntaxGenerator.GetGenerator(document);
                if (generator.GetModifiers(declarator).IsConst)
                    return;
            }

            // If this is an attribute parameter, do not offer the refactoring either
            declarator = literalExpression.FirstAncestorOrSelf<SyntaxNode>(syntaxFacts.IsAttribute);
            if (declarator != null)
                return;

            var isVerbatim = syntaxFacts.IsVerbatimStringLiteral(token);

            context.RegisterRefactoring(
                new MyCodeAction(
                    _ => UpdateDocumentAsync(document, root, literalExpression, isVerbatim)),
                literalExpression.Span);
        }

        private static string GetTextWithoutQuotes(string text, bool isVerbatim)
        {
            // Trim off an extra character (@ symbol) for verbatim strings
            var startIndex = isVerbatim ? 2 : 1;
            return text[startIndex..^1];
        }

        private static SyntaxNode CreateInterpolatedString(Document document, SyntaxNode literalExpression, bool isVerbatim)
        {
            var generator = SyntaxGenerator.GetGenerator(document);
            var startToken = generator.CreateInterpolatedStringStartToken(isVerbatim)
                .WithLeadingTrivia(literalExpression.GetLeadingTrivia());
            var endToken = generator.CreateInterpolatedStringEndToken()
                .WithTrailingTrivia(literalExpression.GetTrailingTrivia());

            var text = literalExpression.GetFirstToken().Text;
            var textWithEscapedBraces = text.Replace("{", "{{").Replace("}", "}}");
            var textWithoutQuotes = GetTextWithoutQuotes(textWithEscapedBraces, isVerbatim);
            var newNode = generator.InterpolatedStringText(generator.InterpolatedStringTextToken(textWithoutQuotes));

            return generator.InterpolatedStringExpression(startToken, new[] { newNode }, endToken);
        }

        private static Task<Document> UpdateDocumentAsync(Document document, SyntaxNode root, SyntaxNode literalExpression, bool isVerbatim)
        {
            var interpolatedString = CreateInterpolatedString(document, literalExpression, isVerbatim);
            var newRoot = root.ReplaceNode(literalExpression, interpolatedString);
            return Task.FromResult(document.WithSyntaxRoot(newRoot));
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(FeaturesResources.Convert_to_interpolated_string, createChangedDocument)
            {
            }
        }
    }
}
