namespace Way2.Todo.Api.Tasks;

// Serializados em JSON e persistidos como texto minúsculo: low | medium | high.
public enum TaskPriority
{
    Low,
    Medium,
    High,
}

// Serializados em JSON e persistidos como texto minúsculo: pending | completed.
public enum TodoTaskStatus
{
    Pending,
    Completed,
}
