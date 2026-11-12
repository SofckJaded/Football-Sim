# Engineering Decisions

- **Single Core Assembly (`FootballSim`):** Combined Core, Physics, Locomotion, AI, Tactics, and Rules namespaces into one zero-dependency C# library as recommended in Section 2 to avoid circular project references while keeping strict module boundaries.
- **Godot 4.7.2 .NET (`net8.0`):** Pinned `Godot.NET.Sdk/4.7.2` with `net8.0` for out-of-the-box compatibility with the official Godot 4.7.2 Mono build.
