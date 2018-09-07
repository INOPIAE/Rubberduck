﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using Rubberduck.Parsing.Annotations;
using Rubberduck.Parsing.Symbols;
using Rubberduck.Parsing.UIContext;
using Rubberduck.Parsing.VBA;
using Rubberduck.Parsing.VBA.Extensions;
using Rubberduck.Resources.UnitTesting;
using Rubberduck.VBEditor.ComManagement.TypeLibs;
using Rubberduck.VBEditor.ComManagement.TypeLibsAPI;

namespace Rubberduck.UnitTesting
{
    internal class TestEngine : ITestEngine
    {
        private static readonly ParserState[] AllowedRunStates = new ParserState[]
        {
            ParserState.ResolvedDeclarations,
            ParserState.ResolvingReferences,
            ParserState.Ready
        };
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly RubberduckParserState _state;
        private readonly IFakesFactory _fakesFactory;
        private readonly IVBETypeLibsAPI _typeLibApi;
        private readonly ITypeLibWrapperProvider _wrapperProvider;
        private readonly IUiDispatcher _uiDispatcher;

        private readonly Dictionary<TestMethod, TestOutcome> testResults = new Dictionary<TestMethod, TestOutcome>();
        public IEnumerable<TestMethod> Tests { get; private set; }
        // FIXME consider forcing VBE in design mode here
        public bool CanRun => AllowedRunStates.Contains(_state.Status);

        private bool _testRequested;
        private bool refreshBackoff;


        public TestEngine(RubberduckParserState state, IFakesFactory fakesFactory, IVBETypeLibsAPI typeLibApi, ITypeLibWrapperProvider wrapperProvider, IUiDispatcher uiDispatcher)
        {
            Debug.WriteLine("TestEngine created.");
            _state = state;
            _fakesFactory = fakesFactory;
            _typeLibApi = typeLibApi;
            _wrapperProvider = wrapperProvider;
            _uiDispatcher = uiDispatcher;

            _state.StateChanged += StateChangedHandler;
        }


        public TestOutcome CurrentAggregateOutcome
        {
            get
            {
                if (testResults.Values.Any(o => o == TestOutcome.Failed))
                {
                    return TestOutcome.Failed;
                }
                if (testResults.Values.Any(o => o == TestOutcome.Inconclusive || o == TestOutcome.Ignored))
                {
                    return TestOutcome.Inconclusive;
                }
                if (testResults.Values.Any(o => o == TestOutcome.Succeeded))
                {
                    return TestOutcome.Succeeded;
                }
                // no test values recorded -> no tests run -> unknown
                return TestOutcome.Unknown;
            }
        }

        private void StateChangedHandler(object sender, ParserStateEventArgs e)
        {
            if (!CanRun)
            {
                refreshBackoff = false;
            }
            // CanRun returned true already, only refresh tests if we're not backed off
            else if (!refreshBackoff)
            {
                refreshBackoff = true;
                Tests = TestDiscovery.GetAllTests(_state);
                _uiDispatcher.InvokeAsync(() => TestsRefreshed?.Invoke(this, EventArgs.Empty));

                if (_testRequested)
                {
                    _testRequested = false;
                    _uiDispatcher.InvokeAsync(() =>
                    {
                        RunInternal(Tests);
                    });
                }
            }

            // any error cancels outstanding test runs
            if (e.IsError)
            {
                _testRequested = false;
            }
        }

        public event EventHandler<TestCompletedEventArgs> TestCompleted;
        public event EventHandler<long> TestRunCompleted;
        public event EventHandler TestsRefreshed;

        private void OnTestCompleted(TestMethod test, TestResult result)
        {
            testResults.Add(test, result.Outcome);
            _uiDispatcher.InvokeAsync(() => TestCompleted?.Invoke(this, new TestCompletedEventArgs(test, result)));
        }

        public void Run(IEnumerable<TestMethod> tests)
        {
            _uiDispatcher.InvokeAsync(() => RunInternal(tests));
        }

        private void RunInternal(IEnumerable<TestMethod> tests)
        {
            if (!CanRun)
            {
                return;
            }
            // FIXME we shouldn't need to handle awaiting a certain parser state ourselves anymore, right?
            // that would drop the _testsRequested member completely
            _state.OnSuspendParser(this, AllowedRunStates, () => RunWhileSuspended(tests));
        }

        private void EnsureRubberduckIsReferencedForEarlyBoundTests()
        {
            var projectIdsOfMembersUsingAddInLibrary = _state.DeclarationFinder.AllUserDeclarations
                .Where(member => member.AsTypeName == "Rubberduck.PermissiveAssertClass"
                                 || member.AsTypeName == "Rubberduck.AssertClass")
                .Select(member => member.ProjectId)
                .ToHashSet();
            var projectsUsingAddInLibrary = _state.DeclarationFinder
                .UserDeclarations(DeclarationType.Project)
                .Where(declaration => projectIdsOfMembersUsingAddInLibrary.Contains(declaration.ProjectId))
                .Select(declaration => declaration.Project);

            foreach (var project in projectsUsingAddInLibrary)
            {
                VBEInteraction.EnsureProjectReferencesUnitTesting(project);
            }
        }

        private void RunWhileSuspended(IEnumerable<TestMethod> tests)
        {
            // FIXME do something useful when this fails!
            EnsureRubberduckIsReferencedForEarlyBoundTests();
            var testMethods = tests as IList<TestMethod> ?? tests.ToList();
            if (!testMethods.Any())
            {
                return;
            }
            testResults.Clear();
            var overallTime = new Stopwatch();
            overallTime.Start();
            try
            {
                var modules = testMethods.GroupBy(test => test.Declaration.QualifiedName.QualifiedModuleName)
                    .Select(grouping => grouping.Key);
                foreach (var qmn in modules)
                {
                    var testInitialize = TestDiscovery.FindTestInitializeMethods(qmn, _state).ToList();
                    var testCleanup = TestDiscovery.FindTestCleanupMethods(qmn, _state).ToList();

                    var moduleTestMethods = testMethods
                        .Where(test =>
                        {
                            var testModuleName = test.Declaration.QualifiedName.QualifiedModuleName;

                            return testModuleName.ProjectId == qmn.ProjectId
                                   && testModuleName.ComponentName == qmn.ComponentName;
                        });

                    var fakes = _fakesFactory.Create();
                    var initializeMethods = TestDiscovery.FindModuleInitializeMethods(qmn, _state);
                    using (var typeLibWrapper = _wrapperProvider.TypeLibWrapperFromProject(qmn.ProjectId))
                    {
                        try
                        {
                            VBEInteraction.RunDeclarations(_typeLibApi, typeLibWrapper, initializeMethods);
                        }
                        catch (COMException ex)
                        {
                            Logger.Error(ex,
                                "Unexpected COM exception while initializing tests for module {0}. The module will be skipped.",
                                qmn.Name);
                            foreach (var method in moduleTestMethods)
                            {
                                OnTestCompleted(method, new TestResult(TestOutcome.Unknown, AssertMessages.Assert_ComException));
                            }
                            continue;
                        }
                        foreach (var test in moduleTestMethods)
                        {
                            // no need to run setup/teardown for ignored tests
                            if (test.Declaration.Annotations.Any(a => a.AnnotationType == AnnotationType.IgnoreTest))
                            {
                                OnTestCompleted(test, new TestResult(TestOutcome.Ignored));
                                continue;
                            }

                            try
                            {
                                fakes.StartTest();
                                try
                                {
                                    VBEInteraction.RunDeclarations(_typeLibApi, typeLibWrapper, testInitialize);
                                }
                                catch (Exception trace)
                                {
                                    OnTestCompleted(test, new TestResult(TestOutcome.Inconclusive, AssertMessages.Assert_TestInitializeFailure));
                                    Logger.Trace(trace, "Unexpected Exception when running TestInitialize");
                                    continue;
                                }
                                var result = RunTestMethod(typeLibWrapper, test);
                                // we can trigger this event, because cleanup can fail without affecting the result
                                OnTestCompleted(test, result);
                                VBEInteraction.RunDeclarations(_typeLibApi, typeLibWrapper, testCleanup);
                            }
                            catch (COMException ex)
                            {
                                Logger.Error(ex, "Unexpected COM exception while running tests.");
                                OnTestCompleted(test, new TestResult(TestOutcome.Inconclusive, AssertMessages.Assert_ComException));
                            }
                            finally
                            {
                                fakes.StopTest();
                            }
                        }
                        var cleanupMethods = TestDiscovery.FindModuleCleanupMethods(qmn, _state);
                        try
                        {
                            VBEInteraction.RunDeclarations(_typeLibApi, typeLibWrapper, cleanupMethods);
                        }
                        catch (COMException ex)
                        {
                            Logger.Error(ex,
                                "Unexpected COM exception while cleaning up tests for module {0}. Aborting any further unit tests",
                                qmn.Name);
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unexpected expection while running unit tests; unit tests will be aborted");
            }
            overallTime.Stop();
            TestRunCompleted?.Invoke(this, overallTime.ElapsedMilliseconds);
        }
        
        private TestResult RunTestMethod(ITypeLibWrapper typeLib, TestMethod test)
        {
            var assertResults = new List<AssertCompletedEventArgs>();
            if (!VBEInteraction.AttemptRunTestMethod(_typeLibApi, typeLib, test, (s, e) => assertResults.Add(e), out var duration))
            {
                // FIXME i18n
                return new TestResult(TestOutcome.Inconclusive, "Test raised an error.", duration);
            }
            return EvaluateResults(assertResults, duration);
        }

        private TestResult EvaluateResults(IEnumerable<AssertCompletedEventArgs> assertResults, long duration)
        {
            var result = new AssertCompletedEventArgs(TestOutcome.Succeeded);

            if (assertResults.Any(assertion => assertion.Outcome != TestOutcome.Succeeded))
            {
                result = assertResults.First(assertion => assertion.Outcome != TestOutcome.Succeeded);
            }

            return new TestResult(result.Outcome, result.Message, duration);
        }
    }
}
