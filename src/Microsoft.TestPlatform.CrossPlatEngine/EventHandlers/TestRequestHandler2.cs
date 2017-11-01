// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CommunicationUtilities
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.EventHandlers;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.DataCollection.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.EventHandlers;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine.TesthostProtocol;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
    using Microsoft.VisualStudio.TestPlatform.Utilities;
    using CrossPlatResources = Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Resources.Resources;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine;

    public class TestRequestHandler : IDisposable, ITestRequestHandler
    {
        private readonly IDataSerializer dataSerializer;
        private ITestHostManagerFactory testHostManagerFactory;
        private ICommunicationEndPoint communicationEndPoint;
        private int protocolVersion = 1;

        private TestHostConnectionInfo connectionInfo;

        private int highestSupportedVersion = 2;
        private JobQueue<Action> jobQueue;
        private ICommunicationChannel channel;

        private ManualResetEventSlim requestSenderConnected;

        private ManualResetEventSlim sessionCompleted;

        private Action<Message> onAckMessageRecieved;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestRequestHandler2" />.
        /// </summary>
        public TestRequestHandler(TestHostConnectionInfo connectionInfo) : this(connectionInfo, JsonDataSerializer.Instance)
        {
        }

        protected TestRequestHandler(TestHostConnectionInfo connectionInfo, ICommunicationEndPoint communicationEndpoint, IDataSerializer dataSerializer, JobQueue<Action> jobQueue)
        {
            this.communicationEndPoint = communicationEndpoint;
            this.connectionInfo = connectionInfo;
            this.dataSerializer = dataSerializer;
            this.requestSenderConnected = new ManualResetEventSlim(false);
            this.sessionCompleted = new ManualResetEventSlim(false);
            this.onAckMessageRecieved = (message) => { throw new NotImplementedException(); };
            this.jobQueue = jobQueue;
        }

        protected TestRequestHandler(TestHostConnectionInfo connectionInfo, IDataSerializer dataSerializer)
        {
            this.connectionInfo = connectionInfo;
            this.dataSerializer = dataSerializer;
            this.requestSenderConnected = new ManualResetEventSlim(false);
            this.sessionCompleted = new ManualResetEventSlim(false);
            this.onAckMessageRecieved = (message) => { throw new NotImplementedException(); };

            if (connectionInfo.Role == ConnectionRole.Host)
            {
                this.communicationEndPoint = new SocketServer();
            }
            else
            {
                this.communicationEndPoint = new SocketClient();
            }

            this.jobQueue = new JobQueue<Action>(
                (action) => { action(); },
                "TestHostOperationQueue",
                500,
                25000000,
                true,
                (message) => EqtTrace.Error(message));
        }

        /// <inheritdoc />
        public void InitializeCommunication()
        {
            this.communicationEndPoint.Connected += (sender, connectedArgs) =>
            {
                if (!connectedArgs.Connected)
                {
                    throw connectedArgs.Fault;
                }
                this.channel = connectedArgs.Channel;
                this.channel.MessageReceived += this.OnMessageReceived;
                requestSenderConnected.Set();
            };

            this.communicationEndPoint.Start(connectionInfo.Endpoint);
        }

        /// <inheritdoc />
        public bool WaitForRequestSenderConnection(int connectionTimeout)
        {
            return requestSenderConnected.Wait(connectionTimeout);
        }

        /// <inheritdoc />
        public void ProcessRequests(ITestHostManagerFactory testHostManagerFactory)
        {
            this.testHostManagerFactory = testHostManagerFactory;
            this.sessionCompleted.Wait();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.communicationEndPoint.Stop();
            this.channel?.Dispose();
        }

        /// <inheritdoc />
        public void Close()
        {
            this.Dispose();
        }

        /// <inheritdoc />
        public void SendTestCases(IEnumerable<TestCase> discoveredTestCases)
        {
            var data = this.dataSerializer.SerializePayload(MessageType.TestCasesFound, discoveredTestCases, this.protocolVersion);
            this.channel.Send(data);
        }

        /// <inheritdoc />
        public void SendTestRunStatistics(TestRunChangedEventArgs testRunChangedArgs)
        {
            var data = this.dataSerializer.SerializePayload(MessageType.TestRunStatsChange, testRunChangedArgs, this.protocolVersion);
            this.channel.Send(data);
        }

        /// <inheritdoc />
        public void SendLog(TestMessageLevel messageLevel, string message)
        {
            var data = this.dataSerializer.SerializePayload(
                    MessageType.TestMessage,
                    new TestMessagePayload { MessageLevel = messageLevel, Message = message },
                    this.protocolVersion);
            this.channel.Send(data);
        }

        /// <inheritdoc />
        public void SendExecutionComplete(
                TestRunCompleteEventArgs testRunCompleteArgs,
                TestRunChangedEventArgs lastChunkArgs,
                ICollection<AttachmentSet> runContextAttachments,
                ICollection<string> executorUris)
        {
            var data = this.dataSerializer.SerializePayload(
                    MessageType.ExecutionComplete,
                    new TestRunCompletePayload
                    {
                        TestRunCompleteArgs = testRunCompleteArgs,
                        LastRunTests = lastChunkArgs,
                        RunAttachments = runContextAttachments,
                        ExecutorUris = executorUris
                    },
                    this.protocolVersion);
            this.channel.Send(data);
        }

        /// <inheritdoc />
        public void DiscoveryComplete(DiscoveryCompleteEventArgs discoveryCompleteEventArgs, IEnumerable<TestCase> lastChunk)
        {
            var data = this.dataSerializer.SerializePayload(
                    MessageType.DiscoveryComplete,
                    new DiscoveryCompletePayload
                    {
                        TotalTests = discoveryCompleteEventArgs.TotalCount,
                        LastDiscoveredTests = discoveryCompleteEventArgs.IsAborted ? null : lastChunk,
                        IsAborted = discoveryCompleteEventArgs.IsAborted,
                        Metrics = discoveryCompleteEventArgs.Metrics
                    },
                    this.protocolVersion);
            this.channel.Send(data);
        }

        /// <inheritdoc />
        public int LaunchProcessWithDebuggerAttached(TestProcessStartInfo testProcessStartInfo)
        {
            return 0;
        }

        private ITestCaseEventsHandler GetTestCaseEventsHandler(string runSettings)
        {
            ITestCaseEventsHandler testCaseEventsHandler = null;

            // Listen to test case events only if data collection is enabled
            if ((XmlRunSettingsUtilities.IsDataCollectionEnabled(runSettings) && DataCollectionTestCaseEventSender.Instance != null) || XmlRunSettingsUtilities.IsInProcDataCollectionEnabled(runSettings))
            {
                testCaseEventsHandler = new TestCaseEventsHandler();
            }

            return testCaseEventsHandler;
        }

        public void OnMessageReceived(object sender, MessageReceivedEventArgs messageReceivedArgs)
        {
            var message = this.dataSerializer.DeserializeMessage(messageReceivedArgs.Data);

            switch (message.MessageType)
            {
                case MessageType.VersionCheck:
                    var version = this.dataSerializer.DeserializePayload<int>(message);
                    this.protocolVersion = Math.Min(version, highestSupportedVersion);

                    // Send the negotiated protocol to request sender
                    this.channel.Send(this.dataSerializer.SerializePayload(MessageType.VersionCheck, this.protocolVersion));

                    // Can only do this after InitializeCommunication because TestHost cannot "Send Log" unless communications are initialized
                    if (!string.IsNullOrEmpty(EqtTrace.LogFile))
                    {
                        this.SendLog(TestMessageLevel.Informational, string.Format(CrossPlatResources.TesthostDiagLogOutputFile, EqtTrace.LogFile));
                    }
                    else if (!string.IsNullOrEmpty(EqtTrace.ErrorOnInitialization))
                    {
                        this.SendLog(TestMessageLevel.Warning, EqtTrace.ErrorOnInitialization);
                    }
                    break;

                case MessageType.DiscoveryInitialize:
                    {
                        EqtTrace.Info("Discovery Session Initialize.");
                        var pathToAdditionalExtensions = message.Payload.ToObject<IEnumerable<string>>();
                        jobQueue.QueueJob(
                                () =>
                                testHostManagerFactory.GetDiscoveryManager().Initialize(pathToAdditionalExtensions), 0);
                        //jobQueue.Flush();
                        break;
                    }

                case MessageType.StartDiscovery:
                    {
                        EqtTrace.Info("Discovery started.");

                        var discoveryEventsHandler = new TestDiscoveryEventHandler(this);
                        var discoveryCriteria = message.Payload.ToObject<DiscoveryCriteria>();
                        jobQueue.QueueJob(
                                () =>
                                testHostManagerFactory.GetDiscoveryManager()
                                .DiscoverTests(discoveryCriteria, discoveryEventsHandler), 0);

                        break;
                    }

                case MessageType.ExecutionInitialize:
                    {
                        EqtTrace.Info("Discovery Session Initialize.");
                        var pathToAdditionalExtensions = message.Payload.ToObject<IEnumerable<string>>();
                        jobQueue.QueueJob(
                                () =>
                                testHostManagerFactory.GetExecutionManager().Initialize(pathToAdditionalExtensions), 0);
                        //jobQueue.Flush();
                        break;
                    }

                case MessageType.StartTestExecutionWithSources:
                    {
                        EqtTrace.Info("Execution started.");
                        var testRunEventsHandler = new TestRunEventsHandler(this);

                        var testRunCriteriaWithSources = message.Payload.ToObject<TestRunCriteriaWithSources>();
                        jobQueue.QueueJob(
                                () =>
                                testHostManagerFactory.GetExecutionManager()
                                .StartTestRun(
                                    testRunCriteriaWithSources.AdapterSourceMap,
                                    testRunCriteriaWithSources.Package,
                                    testRunCriteriaWithSources.RunSettings,
                                    testRunCriteriaWithSources.TestExecutionContext,
                                    this.GetTestCaseEventsHandler(testRunCriteriaWithSources.RunSettings),
                                    testRunEventsHandler),
                                0);

                        break;
                    }

                case MessageType.StartTestExecutionWithTests:
                    {
                        EqtTrace.Info("Execution started.");
                        var testRunEventsHandler = new TestRunEventsHandler(this);

                        var testRunCriteriaWithTests =
                            this.dataSerializer.DeserializePayload<TestRunCriteriaWithTests>(message);

                        jobQueue.QueueJob(
                                () =>
                                testHostManagerFactory.GetExecutionManager()
                                .StartTestRun(
                                    testRunCriteriaWithTests.Tests,
                                    testRunCriteriaWithTests.Package,
                                    testRunCriteriaWithTests.RunSettings,
                                    testRunCriteriaWithTests.TestExecutionContext,
                                    this.GetTestCaseEventsHandler(testRunCriteriaWithTests.RunSettings),
                                    testRunEventsHandler),
                                0);

                        break;
                    }

                case MessageType.CancelTestRun:
                    jobQueue.Pause();
                    testHostManagerFactory.GetExecutionManager().Cancel();
                    break;

                case MessageType.LaunchAdapterProcessWithDebuggerAttachedCallback:
                    this.onAckMessageRecieved?.Invoke(message);
                    break;

                case MessageType.AbortTestRun:
                    jobQueue.Pause();
                    testHostManagerFactory.GetExecutionManager().Abort();
                    break;

                case MessageType.SessionEnd:
                    {
                        EqtTrace.Info("Session End message received from server. Closing the connection.");
                        sessionCompleted.Set();
                        this.Close();
                        break;
                    }

                case MessageType.SessionAbort:
                    {
                        // Dont do anything for now.
                        break;
                    }

                default:
                    {
                        EqtTrace.Info("Invalid Message types");
                        break;
                    }
            }
        }
    }
}