using Marten.Tools;

namespace Marten.Agents;

public sealed partial class AgentManager {
	// Lock ordering: taskGate before any individual agent gate. Registration, retargeting,
	// completion and its entire fanout share a single order. No callbacks under taskGate.
	readonly object taskGate = new();

	internal (TaskItem Before, TaskItem After) UpdateTask(Agent owner, int id, TaskState state, string? reason, TaskDependency? waitingOn) {
		HashSet<Agent> changed = [owner];
		(TaskItem Before, TaskItem After) result;
		lock(taskGate) {
			if(waitingOn != null) {
				if(state != TaskState.Waiting) throw new ToolException("A task dependency requires the waiting state.");
				if(ValidateDependency(owner, id, waitingOn).State == TaskState.Done)
					(state, reason, waitingOn) = (TaskState.Open, null, null);
			}
			result = owner.SetTaskState(id, state, reason, waitingOn);
			if(state == TaskState.Done) {
				var dependency = new TaskDependency(owner.AgentId, id);
				foreach(var agent in agents.Values.Where(a => CanDependOn(a, owner))) {
					foreach(var task in agent.Tasks.Where(t => t.State == TaskState.Waiting && t.WaitingOn == dependency)) {
						Reopen(agent, task, owner, result.After);
						changed.Add(agent);
					}
				}
			}
		}
		foreach(var agent in changed) Publish(new { type = "tasks", agentId = agent.AgentId });
		return result;
	}

	static bool CanDependOn(Agent owner, Agent target) => owner == target ||
		(owner.Info.ProjectId != null && owner.Info.ProjectId == target.Info.ProjectId);

	TaskItem ValidateDependency(Agent owner, int id, TaskDependency dependency) {
		HashSet<TaskDependency> seen = [new(owner.AgentId, id)];
		TaskItem? first = null;
		for(var edge = dependency; ; ) {
			if(!seen.Add(edge)) throw new ToolException("That task dependency would create a cycle.");
			if(string.IsNullOrWhiteSpace(edge.AgentId)) throw new ToolException("A task dependency needs an agent id.");
			var target = edge.AgentId == owner.AgentId ? owner : Get(edge.AgentId);
			if(target == null || !CanDependOn(owner, target)) throw new ToolException("The prerequisite agent must be on the same project (or yourself).");
			var task = target.GetTask(edge.TaskId) ?? throw new ToolException($"{target.Info.Name} has no task {edge.TaskId}.");
			first ??= task;
			if(task.State != TaskState.Waiting || task.WaitingOn == null) return first;
			edge = task.WaitingOn;
		}
	}

	static void Reopen(Agent agent, TaskItem task, Agent target, TaskItem done) {
		var note = string.IsNullOrWhiteSpace(done.Reason) ? "" : $" Completion note: {done.Reason}";
		agent.SetTaskState(task.Id, TaskState.Open, null, null,
			$"Your task #{task.Id} \"{task.Title}\" is open again: its prerequisite #{done.Id} \"{done.Title}\" on {Label(target)} is done.{note}");
	}

	// The target's done write is durable before the first waiter is reopened. If a crash
	// interrupted fanout, the remaining edges themselves are the recovery work list.
	void ReconcileTaskDependencies() {
		lock(taskGate) {
			foreach(var owner in agents.Values)
				foreach(var task in owner.Tasks)
					if(task.State == TaskState.Waiting && task.WaitingOn is { AgentId: not null } edge && Get(edge.AgentId) is { } target &&
						CanDependOn(owner, target) && target.GetTask(edge.TaskId) is { State: TaskState.Done } done)
						Reopen(owner, task, target, done);
		}
	}
}
