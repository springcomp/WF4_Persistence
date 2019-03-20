using System;
using System.Activities;
using System.Activities.DurableInstancing;
using System.Configuration;
using System.Runtime.DurableInstancing;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Workflow
{
    /*
    -- Cleanup database by running the following commands
    
    DELETE FROM [System.Activities.DurableInstancing].[IdentityOwnerTable]
    DELETE FROM [System.Activities.DurableInstancing].[RunnableInstancesTable]
    DELETE FROM [System.Activities.DurableInstancing].[InstancesTable]
    DELETE FROM [System.Activities.DurableInstancing].[LockOwnersTable]
    
    */

    class Program
    {
        static void Main(string[] args)
        {
            var store = CreateInstanceStore(out var handle);

            try
            {
                // running Workflow

                var instanceId = RunWorkflow(store);

                var succeeded = WaitForRunnableInstance(store, handle, TimeSpan.FromSeconds(15.0));
                if (!succeeded)
                {
                    Console.Error.WriteLine("Unable to find runnable instances. Aborting.");
                    return;
                }

                // resuming Workflow after unload

                ResumeRunnableInstance(store);


                while (true)
                {
                    Console.WriteLine();
                    Console.WriteLine("Please, press ENTER to exit or type a bookmark name and ENTER to resume the specified bookmark.");

                    var bookmarkName = Console.ReadLine();

                    if (String.IsNullOrEmpty(bookmarkName))
                        return;

                    var has = WaitForRunnableInstance(store, handle, TimeSpan.FromSeconds(15.0));

                    if (has)
                    {
                        try
                        {
                            ResumeRunnableInstance(store);
                        }
                        catch (InstanceNotReadyException)
                        {
                            // this is a bug
                            // there should not be a runnable instance event
                            // when the workflow is waiting for resumption on a bookmark

                            Console.Error.WriteLine("An attempt to resume a runnable instance has failed.");
                            Console.WriteLine($"Resuming workflow with bookmark: \"{bookmarkName}\".");
                            ResumeWorkflow(store, instanceId, bookmarkName);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Resuming workflow with bookmark: \"{bookmarkName}\".");
                        ResumeWorkflow(store, instanceId, bookmarkName);
                    }
                }

                Console.WriteLine("Done.");
            }
            finally
            {
            }
        }

        private static readonly XName WorkflowHostTypePropertyName =
            XNamespace
                .Get("urn:schemas-microsoft-com:System.Activities/4.0/properties")
                .GetName("WorkflowHostType");

        private static readonly XName WorkflowHostType =
            XName.Get("SpringCompWorkflowHost");

        private static readonly IDictionary<XName, object> InstanceValues =
            new Dictionary<XName, object>
            {
                { WorkflowHostTypePropertyName, WorkflowHostType },

            };

        #region Implementation

        private static void ResumeWorkflow(InstanceStore store, Guid instanceId, string bookmarkName)
        {
            var app = CreateWorkflow(store);
            app.Load(instanceId);

            System.Diagnostics.Debug.Assert(app.Id == instanceId);

            app.ResumeBookmark(bookmarkName, new object(), TimeSpan.FromSeconds(10.0));
        }

        private static Guid RunWorkflow(InstanceStore store)
        {
            var app = CreateWorkflow(store);
            app.Run();

            return app.Id;
        }

        private static void ResumeRunnableInstance(InstanceStore store)
        {
            var app = CreateWorkflow(store);
            app.LoadRunnableInstance();
            app.Run();
        }

        private static WorkflowApplication CreateWorkflow(InstanceStore store)
        {
            var activity = new WorkflowApp();

            var app = new WorkflowApplication(activity);
            app.AddInitialInstanceValues(InstanceValues);
            app.InstanceStore = store;

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
                foreach (var text in e.Reason.Message.Split(new []{'.'}, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()))
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
            var command = new CreateWorkflowOwnerCommand
            {
                InstanceOwnerMetadata = { { WorkflowHostTypePropertyName, new InstanceValue(WorkflowHostType) }, },
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
}
