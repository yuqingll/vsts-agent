using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [DataContract]

    public sealed class AgentJobRequestMessage2
    {
        [JsonConstructor]
        internal AgentJobRequestMessage2()
        {
            this.MessageType = JobRequestMessageTypes.AgentJobRequest;
        }

        public AgentJobRequestMessage2(
            TaskOrchestrationPlanReference plan,
            TimelineReference timeline,
            Guid jobId,
            String jobName,
            String jobRefName,
            JobEnvironment environment,
            IEnumerable<TaskInstance> tasks)
        {
            m_tasks = new List<TaskInstance>(tasks);
            this.MessageType = JobRequestMessageTypes.AgentJobRequest;
            this.Plan = plan;
            this.JobId = jobId;
            this.JobName = jobName;
            this.JobRefName = jobRefName;
            this.Timeline = timeline;
            this.Environment = environment;
        }

        [DataMember]
        public Int64 RequestId
        {
            get;
            internal set;
        }

        [DataMember]
        public Guid LockToken
        {
            get;
            internal set;
        }

        [DataMember]
        public DateTime LockedUntil
        {
            get;
            internal set;
        }

        public ReadOnlyCollection<TaskInstance> Tasks
        {
            get
            {
                if (m_tasks == null)
                {
                    m_tasks = new List<TaskInstance>();
                }
                return m_tasks.AsReadOnly();
            }
        }

        [DataMember]
        public String MessageType
        {
            get;
            private set;
        }

        [DataMember]
        public TaskOrchestrationPlanReference Plan
        {
            get;
            private set;
        }

        [DataMember]
        public TimelineReference Timeline
        {
            get;
            private set;
        }

        [DataMember]
        public Guid JobId
        {
            get;
            private set;
        }

        [DataMember]
        public String JobName
        {
            get;
            private set;
        }

        [DataMember]
        public String JobRefName
        {
            get;
            private set;
        }

        [DataMember]
        public JobEnvironment Environment
        {
            get;
            private set;
        }

        [DataMember(Name = "Tasks", EmitDefaultValue = false)]
        private List<TaskInstance> m_tasks;
    }

    public class TaskStep
    {
        [DataMember]
        public Guid InstanceId { get; set; }
        [DataMember]
        public string DisplayName { get; set; }
        [DataMember]
        public bool Enabled { get; set; }
        [DataMember]
        public string Condition { get; set; }
        [DataMember]
        public bool ContinueOnError { get; set; }
        [DataMember]
        public bool AlwaysRun { get; set; }
        [DataMember]
        public int TimeoutInMinutes { get; set; }
        [DataMember]
        public string RefName { get; set; }
        public IDictionary<string, string> Environment { get; }
    }

    public class GroupStep
    {
        public List<TaskStep> Steps { get; set; }
        public Container
    }
}