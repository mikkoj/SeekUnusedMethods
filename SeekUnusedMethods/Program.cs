using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Roslyn.Compilers.Common;
using Roslyn.Compilers.CSharp;
using Roslyn.Services;


namespace SeekUnusedMethods
{
    class Program
    {
        private static readonly ConcurrentBag<ISymbol> CheckedSymbols = new ConcurrentBag<ISymbol>();
        private static readonly ConcurrentBag<ISymbol> UnusedMethodSymbols = new ConcurrentBag<ISymbol>();
        private static readonly IDictionary<string, ISemanticModel> SemanticModels = new ConcurrentDictionary<string, ISemanticModel>();
        private static readonly IDictionary<InvocationExpressionSyntax, ISemanticInfo> MethodSemanticInfos = new ConcurrentDictionary<InvocationExpressionSyntax, ISemanticInfo>();
        private static readonly IDictionary<string, IEnumerable<InvocationExpressionSyntax>> MethodInvocations = new ConcurrentDictionary<string, IEnumerable<InvocationExpressionSyntax>>();

        static void Main(string[] args)
        {
            var parsedArgs = ParseArgs(args);
            if (parsedArgs == null)
            {
                Console.WriteLine("First argument must be a solution file!");
                return;
            }

            Console.WriteLine("Searching for unused methods in {0}\n", parsedArgs.SolutionFile);
            var watch = new Stopwatch();
            watch.Start();
            FindUnusedMethodsInSolution(parsedArgs.SolutionFile, parsedArgs.IgnoreNamespaces);
            watch.Stop();
            Console.WriteLine("\n-- done in {0}s.", watch.Elapsed.TotalSeconds);

            PrintResults();

            Console.Read();
        }

        private static Args ParseArgs(IList<string> arguments)
        {
            try
            {
                var parsedArgs = new Args { SolutionFile = arguments[0] };
                if (arguments.Count() > 1)
                {
                    parsedArgs.IgnoreNamespaces = new List<string>(arguments[1].Split(','));
                }

                if (parsedArgs.SolutionFile.EndsWith(".sln") == false ||
                    File.Exists(parsedArgs.SolutionFile) == false)
                {
                    return null;
                }

                return parsedArgs;

            }
            catch
            {
                return null;
            }
        }


        private static void PrintResults()
        {
            Console.WriteLine("-- found {0} unused methods", UnusedMethodSymbols.Count);

            const string resultFile = "UnusedMethods.txt";
            using (var file = File.CreateText(resultFile))
            {
                foreach (var method in UnusedMethodSymbols)
                {
                    var assemblyName = " ";
                    if (method.ContainingAssembly != null)
                    {
                        assemblyName = method.ContainingAssembly.Name;
                    }
                    file.WriteLine("{0}\t{1}", assemblyName, method.ToDisplayString());
                }
            }
            Console.WriteLine("-- wrote to file: {0}", resultFile);
        }


        private static void FindUnusedMethodsInSolution(string solutionFile, ICollection<string> ignoreNamespaces)
        {
            try
            {
                ISolution solution = Solution.Load(solutionFile);
                foreach (IProject project in solution.Projects)
                {
                    if (ignoreNamespaces.Contains(project.DisplayName))
                    {
                        continue;
                    }

                    Console.WriteLine("-> processing {0}", project.AssemblyName);
                    foreach (IDocument document in project.Documents)
                    {
                        FindUnusedMethodsInDocument(document, solution);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unable to process {0} - {1}", solutionFile, ex.Message);
            }
        }


        private static void FindUnusedMethodsInDocument(IDocument document, ISolution solution)
        {
            var semanticModel = GetSemanticModelForDocument(document);
            var methods = GetMethodDeclarationsInDocument(document);

            foreach (var methodsDecl in methods)
            {
                var methodDeclSymbol = semanticModel.GetDeclaredSymbol(methodsDecl) as MethodSymbol;

                // Is this symbol used anywhere in the solution?
                if (CheckedSymbols.Contains(methodDeclSymbol) == false &&
                    IsMethodInvokedInSolution(solution, methodDeclSymbol) == false)
                {
                    UnusedMethodSymbols.Add(methodDeclSymbol);
                }
            }
        }


        private static bool IsMethodInvokedInSolution(ISolution solution, MethodSymbol declSymbol)
        {
            if (declSymbol.IsGenericMethod ||
                declSymbol.TypeParameters.Any() ||
                declSymbol.Parameters.ToList().Exists(p=> p.Type.ToString().Contains(typeof(EventArgs).Name)))
            {
                // Let's skip generic methods and events for now
                return true;
            }

            foreach (IProject project in solution.Projects)
            {
                foreach (IDocument document in project.Documents)
                {
                    var semanticModel = GetSemanticModelForDocument(document);
                    var methodInvocations = GetMethodInvocationsInDocument(document);

                    foreach (var methodInvocation in methodInvocations)
                    {
                        var methodInvocationInfo = GetSemanticInfoForMethod(methodInvocation, semanticModel);

                        // We'll try to choose candidate symbols, if default is null
                        var invocSymbol = methodInvocationInfo.Symbol as MethodSymbol ??
                                          methodInvocationInfo.CandidateSymbols.FirstOrDefault() as MethodSymbol;
                        if (invocSymbol != null &&
                            invocSymbol.Name == declSymbol.Name &&
                            invocSymbol.ReturnType == declSymbol.ReturnType &&
                            invocSymbol.Parameters.ToString() == declSymbol.Parameters.ToString())
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }


        private static ISemanticInfo GetSemanticInfoForMethod(InvocationExpressionSyntax methodInvocation, ISemanticModel semanticModel)
        {
            ISemanticInfo semanticInfo;
            if (MethodSemanticInfos.TryGetValue(methodInvocation, out semanticInfo))
            {
                return semanticInfo;
            }
            semanticInfo = semanticModel.GetSemanticInfo(methodInvocation);
            MethodSemanticInfos.Add(methodInvocation, semanticInfo);
            return semanticInfo;
        }


        private static ISemanticModel GetSemanticModelForDocument(IDocument document)
        {
            ISemanticModel semanticModel;
            if (SemanticModels.TryGetValue(document.Id.FileName, out semanticModel))
            {
                return semanticModel;
            }
            semanticModel = document.GetSemanticModel();
            SemanticModels.Add(document.Id.FileName, semanticModel);
            return semanticModel;
        }


        private static IEnumerable<MethodDeclarationSyntax> GetMethodDeclarationsInDocument(IDocument document)
        {
            return document.GetSyntaxTree()
                           .Root
                           .DescendentNodes()
                           .OfType<MethodDeclarationSyntax>()
                           .ToList();
        }


        private static IEnumerable<InvocationExpressionSyntax> GetMethodInvocationsInDocument(IDocument document)
        {
            IEnumerable<InvocationExpressionSyntax> methodInvocations;
            if (MethodInvocations.TryGetValue(document.Id.FileName, out methodInvocations))
            {
                return methodInvocations;
            }

            methodInvocations = document.GetSyntaxTree()
                                        .Root
                                        .DescendentNodes()
                                        .OfType<InvocationExpressionSyntax>()
                                        .ToList();

            MethodInvocations.Add(document.Id.FileName, methodInvocations);
            return methodInvocations;
        }
    }

    public class Args
    {
        public string SolutionFile { get; set; }
        public List<string> IgnoreNamespaces { get; set; }
    }
}