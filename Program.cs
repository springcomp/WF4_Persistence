using System;
using System.Activities;
using System.Activities.DurableInstancing;
using System.Activities.Persistence;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Linq;
using System.Runtime.DurableInstancing;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Workflow
{
    // https://social.msdn.microsoft.com/Forums/en-US/8a424c0b-44f8-4670-8d64-9c7142117b55/instancestore-waitforevents-not-firing-when-using-workflowapplication-with-a-workflowidentity?forum=wfprerelease 
    /*
    -- Cleanup database by running the following commands
    
    DELETE FROM [System.Activities.DurableInstancing].[IdentityOwnerTable]
    DELETE FROM [System.Activities.DurableInstancing].[RunnableInstancesTable]
    DELETE FROM [System.Activities.DurableInstancing].[InstancesTable]
    DELETE FROM [System.Activities.DurableInstancing].[LockOwnersTable]
    
    */

    class Program
    {
        private static readonly string BookmarkPattern = @"(?<app>[^\ ]+)(?:\ +(?<instance>.*))?";
        private static readonly Regex BookmarkRegex = new Regex(BookmarkPattern, RegexOptions.Compiled | RegexOptions.Singleline);

        // This program runs multiple threads:

        // RunnableInstancesDetectionThread
        //      This thread periodically polls the database and waits for workflow instances
        //      in the "runnable" state. When that happens, it raises an event and pauses
        //      until runnable instances have been processed (by another thread).
        //
        // ResumptionBookmarkThread
        //      This is the user interface thread using console input for a user to type commands.
        //      It implements a REPL (read-eval-print-loop) environment where a user can issue
        //      bookmark resumption commands. When this happens, the thread raises an event
        //      for bookmarks to be processed (by another thread).
        //      Valid commands have the following format:
        //      <app-name>[ <instance-id>]
        //
        // ProcessingThread
        //      This thread waits for events raised from other sources.
        //      Valid events are:
        //      - HasRunnableInstances 
        //      - HasBookmark
        //
        //      When "HasBookmark" is raised, the thread resumes the
        //      corresponding in-memory workflow instance.
        //
        //      When "HasRunnableInstance" is raised, the thread attempts
        //      to call the "LoadRunnableInstance" method to load the
        //      corresponding workflow instance from the database.
        //      This must be done until the "LoadRunnableInstance" method
        //      raises and "InstanceNotReadyException". This signals that
        //      all runnable instances from the database have been processed.
        //      At this stage, the thread raises an event to resume the
        //      RunnableInstancesDetectionThread.

        // CHALLENGES:
        //
        //      In order to be able to resume a persisted runnable workflow instance
        //      it is necessary to know the workflow identity in order to match it
        //      with the corresponding XAML definition. This sample associates a
        //      unique identity for each XAML definition that is automatically
        //      persisted to the database and can be retrieved on the "HasRunnableInstance" event.
        //
        //      This sample persists and unloads workflow instances when bookmarked.
        //      Therefore, upon resumption, the a new instance is created with the corresponding
        //      XAML definition and the workflow execution is resumed.
        //      In crosscut, we try to prevent workflow instances from being unloaded if another
        //      message is received in a short period of time. Try to prevent workflow instances
        //      from being unloaded with this sample first.

        static void Main(string[] args)
        {
            var cmdLine = CommandLine.Parse(args);
            var crash = cmdLine.Crash;
            var operation = cmdLine.Operation;

            string bookmarkName = null;
            string instanceId = null;

            var hasRunnableInstances = new ManualResetEvent(false);
            var stopping = new ManualResetEvent(false);

            var monitorRunnableInstances = new AutoResetEvent(false);
            var hasBookmark = new AutoResetEvent(false);

            var store = CreateInstanceStore(out var handle);

            try
            {
                // RunnableInstancesDetectionThread
                //      This thread periodically polls the database and waits for workflow instances
                //      in the "runnable" state. When that happens, it raises an event and pauses
                //      until runnable instances have been processed (by another thread).
                //
                var runnableInstancesDetectionThread = new Thread(unused =>
                {
                    while (true)
                    {
                        var status = WaitHandle.WaitAny(new WaitHandle[]
                        {
                            stopping,
                            monitorRunnableInstances,
                        });

                        if (status == 0)
                            break;

                        if (status == 1)
                        {
                            var succeeded = WaitForRunnableInstance(store, handle, TimeSpan.FromSeconds(15.0));
                            if (succeeded)
                            {
                                hasRunnableInstances.Set();
                            }
                            else
                            {
                                monitorRunnableInstances.Set();
                            }
                        }
                    }
                });

                // ResumptionBookmarkThread
                //      This is the user interface thread using console input for a user to type commands.
                //      It implements a REPL (read-eval-print-loop) environment where a user can issue
                //      bookmark resumption commands. When this happens, the thread raises an event
                //      for bookmarks to be processed (by another thread).
                //      Valid commands have the following format:
                //      <app-name>[ <instance-id>]
                //
                var resumptionBookmarkThread = new Thread(unused =>
                {
                    while (true)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Please, press ENTER to exit or type a bookmark name and ENTER to resume the specified bookmark.");

                        var line = Console.ReadLine();
                        if (line == "")
                        {
                            stopping.Set();
                            break;
                        }
                        else
                        {
                            var match = BookmarkRegex.Match(line);
                            if (match.Success)
                            {
                                if (match.Groups["instance"] != null)
                                    instanceId = match.Groups["instance"].Value;
                                bookmarkName = match.Groups["app"].Value;
                                hasBookmark.Set();
                            }
                            else
                            {
                                Console.Error.WriteLine("Invalid syntax.");
                            }
                        }
                    }
                });

                runnableInstancesDetectionThread.IsBackground = true;
                runnableInstancesDetectionThread.Start();

                resumptionBookmarkThread.Start();

                monitorRunnableInstances.Set();

                var instances = new Guid[2] { Guid.Empty, Guid.Empty, };

                // The application starts a instance for two different workflow types
                // Instances are kept in memory for the duration of their lifetime.

                if (operation == CommandLine.Operations.Run)
                {
                    Console.WriteLine("Running instances of two difference workflow apps...");

                    instances[0] = RunWorkflow(store, WorkflowApps.App1);
                    instances[1] = RunWorkflow(store, WorkflowApps.App2);
                }

                // ProcessingThread
                //      This thread waits for events raised from other sources.
                //      Valid events are:
                //      - HasRunnableInstances 
                //      - HasBookmark
                //
                //      When "HasBookmark" is raised, the thread resumes the
                //      corresponding in-memory workflow instance.
                //
                //      When "HasRunnableInstance" is raised, the thread attempts
                //      to call the "LoadRunnableInstance" method to load the
                //      corresponding workflow instance from the database.
                //      This must be done until the "LoadRunnableInstance" method
                //      raises and "InstanceNotReadyException". This signals that
                //      all runnable instances from the database have been processed.
                //      At this stage, the thread raises an event to resume the
                //      RunnableInstancesDetectionThread.

                while (true)
                {
                    var status = WaitHandle.WaitAny(new WaitHandle[]
                    {
                        stopping,
                        hasRunnableInstances,
                        hasBookmark,
                    });

                    if (status == WaitHandle.WaitTimeout)
                        throw new TimeoutException();

                    // stopping
                    if (status == 0)
                        break;

                    // hasRunnableInstances
                    if (status == 1)
                    {
                        try
                        {
                            var instance = WorkflowApplication.GetRunnableInstance(store);
                            if (crash) throw new ApplicationException("CRASHED");
                            LoadRunnableInstance(store, instance);
                        }
                        catch (InstanceNotReadyException)
                        {
                            // no more runnable instances

                            Console.Error.WriteLine("Resuming runnable instances detection.");

                            hasRunnableInstances.Reset();
                            monitorRunnableInstances.Set();
                        }
                    }

                    // hasBookmark
                    if (status == 2)
                    {
                        System.Diagnostics.Debug.Assert(!String.IsNullOrEmpty(bookmarkName));
                        Console.WriteLine($"Resuming workflow with bookmark: \"{bookmarkName}\".");

                        var identifier = Guid.Empty;

                        if (String.IsNullOrEmpty(instanceId))
                        {
                            var index = 0;
                            if (bookmarkName == "App2")
                                index = 1;
                            identifier = instances[index];
                        }
                        else
                        {
                            identifier = Guid.Parse(instanceId);
                        }

                        ResumeWorkflow(store, identifier, bookmarkName);
                    }
                }

                Console.WriteLine("Done.");

                resumptionBookmarkThread.Join();
                runnableInstancesDetectionThread.Join();
            }
            finally
            {
            }
        }

        private static readonly XNamespace Workflow40Namespace =
            XNamespace.Get("urn:schemas-microsoft-com:System.Activities/4.0/properties");

        private static readonly XNamespace Workflow45Namespace =
            XNamespace.Get("urn:schemas-microsoft-com:System.Activities/4.5/properties");

        private static readonly XName WorkflowHostTypePropertyName =
            Workflow40Namespace.GetName("WorkflowHostType");

        private static readonly XName DefinitionIdentityFilterPropertyName =
            Workflow45Namespace.GetName("DefinitionIdentityFilter");

        private static readonly XName DefinitionIdentitiesPropertyName =
            Workflow45Namespace.GetName("DefinitionIdentities");

        private static readonly XName WorkflowHostType =
            Workflow45Namespace.GetName("WorkflowApplication");

        private static readonly Collection<WorkflowIdentity> Identities =
            new Collection<WorkflowIdentity>(new List<WorkflowIdentity>
            {
                new WorkflowIdentity { Name = WorkflowApps.App1.ToString(), Version = new Version(1, 0), },
                new WorkflowIdentity { Name = WorkflowApps.App2.ToString(), Version = new Version(1, 0), },
            });

        private static readonly IDictionary<XName, object> InstanceValues =
            new Dictionary<XName, object>
            {
                { WorkflowHostTypePropertyName, WorkflowHostType },

            };

        #region Implementation

        private static void ResumeWorkflow(InstanceStore store, Guid instanceId, string bookmarkName)
        {
            var appType = (WorkflowApps)Enum.Parse(typeof(WorkflowApps), bookmarkName);
            var app = CreateWorkflow(store, appType);
            app.Load(instanceId);

            System.Diagnostics.Debug.Assert(app.Id == instanceId);

            app.ResumeBookmark(bookmarkName, new object(), TimeSpan.FromSeconds(10.0));
        }

        private static Guid RunWorkflow(InstanceStore store, WorkflowApps appType)
        {
            var app = CreateWorkflow(store, appType);
            app.Run();

            Console.WriteLine($"{appType} instance {app.Id:d} is running.");

            return app.Id;
        }

        private static void LoadRunnableInstance(InstanceStore store, WorkflowApplicationInstance appInstance)
        {
            var app = CreateWorkflow(store, WorkflowApps.App1);
            switch (appInstance.DefinitionIdentity.Name)
            {
                case "App1":
                    break;
                case "App2":
                    app = CreateWorkflow(store, WorkflowApps.App2);
                    break;
            }

            app.Load(appInstance);
            app.Run();
        }

        private static WorkflowApplication CreateWorkflow(InstanceStore store, WorkflowApps appType = WorkflowApps.App1)
        {
            var identity = new WorkflowIdentity { Name = appType.ToString(), Version = new Version(1, 0), };

            Activity activity = new WorkflowApp1();
            if (appType == WorkflowApps.App2)
                activity = new WorkflowApp2();

            var whoAmI = new PP(appType);

            var app = new WorkflowApplication(activity, identity) { InstanceStore = store };
            app.Extensions.Add(whoAmI);

            SetEvents(app);

            return app;
        }

        private static void SetEvents(WorkflowApplication app)
        {
            app.PersistableIdle = e =>
            {
                var action = PersistableIdleAction.Unload;

                switch (action)
                {
                    case PersistableIdleAction.None:
                        Console.WriteLine("EVT: Workflow has been requested to ignore persistence requirements.");
                        break;
                    case PersistableIdleAction.Unload:
                        Console.WriteLine("EVT: Workflow has been requested to unload.");
                        break;
                    case PersistableIdleAction.Persist:
                        Console.WriteLine("EVT: Workflow has been requested to persist its state and proceed.");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                return action;
            };
            app.Idle = e =>
            {
                Console.WriteLine($"EVT: Workflow has become idle.");

                if (e.Bookmarks.Count > 0)
                    Console.WriteLine($"EVT: Waiting for resumption of workflow on bookmark named '{e.Bookmarks[0].BookmarkName}'.");
            };
            app.Unloaded = e =>
            {
                Console.WriteLine("EVT: Workflow has been unloaded.");
            };
            app.Completed = e =>
            {
                Console.WriteLine("EVT: Workflow has completed.");
            };

            app.Aborted = e =>
            {
                Console.Error.WriteLine($"EVT: Aborting workflow:");
                foreach (var text in e.Reason.Message.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()))
                    Console.Error.WriteLine($"EVT: {text}");
            };
        }

        private static InstanceStore CreateInstanceStore()
        {
            var connectionString = ConfigurationManager.ConnectionStrings["WorkflowDb"].ConnectionString;
            var store = new SqlWorkflowInstanceStore(connectionString);

            return store;
        }

        private static InstanceStore CreateInstanceStore(out InstanceHandle handle)
        {
            var connectionString = ConfigurationManager.ConnectionStrings["WorkflowDb"].ConnectionString;
            var store = new SqlWorkflowInstanceStore(connectionString);

            handle = store.CreateInstanceHandle();

            store.DefaultInstanceOwner = CreateWorkflowInstanceOwner(store, handle);

            return store;
        }

        private static bool WaitForRunnableInstance(InstanceStore store, InstanceOwner owner, Guid instanceId, TimeSpan timeout)
        {
            var handle = store.CreateInstanceHandle(owner, instanceId);
            return WaitForRunnableInstance(store, handle, timeout);
        }

        private static bool WaitForRunnableInstance(InstanceStore store, InstanceHandle handle, TimeSpan timeout)
        {
            try
            {
                var storeEvents = store.WaitForEvents(handle, timeout);

                foreach (var storeEvent in storeEvents)
                {
                    if (storeEvent.Equals(HasRunnableWorkflowEvent.Value))
                        return true;
                }

                return false;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private static void ReleaseInstanceStore(InstanceStore store, InstanceOwner owner)
        {
            DeleteWorkflowInstanceOwner(store, owner, Guid.Empty);
        }

        private static InstanceOwner CreateWorkflowInstanceOwner(InstanceStore store)
        {
            InstanceHandle handle = null;

            try
            {
                handle = store.CreateInstanceHandle();
                return CreateWorkflowInstanceOwner(store, handle);
            }
            finally
            {
                handle?.Free();
            }
        }

        private static InstanceOwner CreateWorkflowInstanceOwner(InstanceStore store, InstanceHandle handle)
        {
            var command = new CreateWorkflowOwnerWithIdentityCommand
            {
                InstanceOwnerMetadata =
                {
                    { WorkflowHostTypePropertyName, new InstanceValue(WorkflowHostType) },
                    { DefinitionIdentityFilterPropertyName, new InstanceValue(WorkflowIdentityFilter.Any) },
                    { DefinitionIdentitiesPropertyName, new InstanceValue(new Collection<WorkflowIdentity>()) },
                },
            };

            var owner = store.Execute(handle, command, TimeSpan.FromMinutes(1.0)).InstanceOwner;

            return owner;
        }

        private static void DeleteWorkflowInstanceOwner(InstanceStore store, InstanceOwner owner, Guid instanceId)
        {
            InstanceHandle handle = null;

            var command = new DeleteWorkflowOwnerCommand();

            try
            {
                handle = store.CreateInstanceHandle(owner, instanceId);
                store.Execute(handle, command, TimeSpan.FromMinutes(1.0));
                store.DefaultInstanceOwner = null;
            }
            finally
            {
                handle?.Free();
            }
        }

        #endregion
    }

    public enum WorkflowApps
    {
        App1,
        App2,
    }

    public sealed class PP : PersistenceParticipant, IAmWhoSeeWhatIDidThere
    {
        private readonly WorkflowApps appType_;

        public PP(WorkflowApps appType)
        {
            appType_ = appType;
        }

        public WorkflowApps WhoAmI => appType_;

        protected override void CollectValues(out IDictionary<XName, object> readWriteValues, out IDictionary<XName, object> writeOnlyValues)
        {
            readWriteValues = new Dictionary<XName, object> { { XName.Get("WhoAmI"), appType_.ToString() } };
            base.CollectValues(out readWriteValues, out writeOnlyValues);
        }
    }

    public interface IAmWhoSeeWhatIDidThere
    {
        WorkflowApps WhoAmI { get; }
    }

}
