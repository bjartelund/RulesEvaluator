# RulesEvaluator

A distributed system for evaluating municipal eligibility rules using AI-assisted fact extraction and Prolog-based rule evaluation.

## Overview

RulesEvaluator is a .NET-based platform that combines:
- **Orleans** — distributed virtual actors for scalable rule management and evaluation
- **.NET Aspire** — local infrastructure orchestration with Azure Table Storage emulation
- **DotProlog** — Prolog-based rule engine for logical evaluation
- **Claude API** — AI-powered schema deduction and structured fact extraction

The system ingests unstructured municipal rules as plain text, transforms them into executable Prolog clauses, and evaluates candidate applications against those rules with human-readable decision summaries.

## Project Structure

```
RulesEvaluator/
├── AppHost/              # .NET Aspire orchestrator
├── ServiceDefaults/      # Shared service configuration
├── Silo/                 # Orleans silo host
├── Grains/               # Orleans grain implementations
│   ├── RulesGrain        # Prolog rule storage & execution
│   ├── ApplicationGrain   # Application evaluation state
│   └── RAGContextGrain    # Context/RAG knowledge base
└── dotprolog/            # DotProlog submodule (Prolog engine)
```

## Architecture

### Core Phases

#### Phase 1: Aspire AppHost & Local Infrastructure Setup
Establishes the foundational infrastructure for the system.

- Create a .NET Aspire AppHost with ServiceDefaults project
- Add Azure Table Storage via Azurite emulator
- Configure Orleans cluster to use Azure Table Storage for clustering and grain persistence
- Scaffold ASP.NET Core Minimal API backend linked to the AppHost

#### Phase 2: Core Orleans Grains & Storage Schema
Defines the virtual actor model for distributed rule and application management.

**Key Grains:**
- **RulesRegistryGrain**: Manages active rule sets with version control
  - Stores raw Prolog text and schema definitions
  - Backs data to Azure Table Storage
- **ApplicationEvaluationGrain**: Manages individual application state
  - Tracks incoming text, extracted data, evaluation outcome, and audit trail
  - Persists evaluation history
- **RAGContextGrain**: Mock knowledge base for regulatory context
  - Simulates fetching relevant policy snippets by keyword
  - Supports context-aware evaluation

#### Phase 3: Unstructured Rules Ingestion Pipeline
Transforms plain-text municipal rule books into structured schemas and executable Prolog.

**Flow:**
1. Accept raw rule text via ingestion endpoint
2. Use Claude API with structured output to parse into:
   - JSON schema defining expected application attributes
   - Valid Prolog facts and rules (.pl syntax)
3. Store both rules and schema in RulesRegistryGrain

#### Phase 4: Application Processing & Prolog Evaluation
Ingests applications, enriches them with context, and evaluates against rules.

**Flow:**
1. Accept unstructured application text
2. Query RAGContextGrain for relevant regulatory context
3. Use Claude API to extract structured facts based on inferred schema
4. Execute Prolog engine with application facts + stored rules
5. Return boolean eligibility outcome

#### Phase 5: LLM-Assisted Results & Dashboard
Interprets Prolog outputs and provides human-readable decision summaries.

**Flow:**
1. Feed raw Prolog output + original text to Claude API
2. Generate clear, justified decision explanation for caseworkers
3. Persist complete evaluation record to ApplicationEvaluationGrain
4. Expose state via Aspire and Orleans dashboards for observability

## Getting Started

### Prerequisites

- .NET 10 SDK or later
- Docker (for running Azurite)
- Anthropic API key (for Claude integration)

### Installation

1. Clone the repository:
```bash
git clone <repository-url>
cd RulesEvaluator
```

2. Restore dependencies:
```bash
dotnet restore
```

3. Build the solution:
```bash
dotnet build
```

### Running the Application

1. Start the Aspire AppHost:
```bash
dotnet run --project AppHost
```

This will:
- Start Azurite (Azure Table Storage emulator)
- Initialize Orleans silo
- Launch the Aspire dashboard at `http://localhost:8080`

2. Access the Aspire Dashboard:
Open your browser to `http://localhost:8080` to monitor:
- Running services and grains
- Azure Table Storage state
- Telemetry and logs

### API Endpoints

#### Ingest Rules
```http
POST /api/rules/ingest
Content-Type: application/json

{
  "ruleText": "Applicants must be at least 18 years old..."
}
```

#### Evaluate Application
```http
POST /api/applications/evaluate
Content-Type: application/json

{
  "applicationText": "John Smith is 25 years old...",
  "rulesId": "rules-v1"
}
```

#### Get Evaluation Result
```http
GET /api/applications/{applicationId}
```

## Configuration

### Environment Variables

- `ANTHROPIC_API_KEY` — Claude API key (required for LLM features)
- `AZURE_STORAGE_EMULATOR` — Azurite connection string (auto-configured via Aspire)

### Orleans Configuration

Orleans is configured via Aspire to use:
- **Clustering**: Azure Table Storage
- **Persistence**: Azure Table Storage
- **Grain state serialization**: System.Text.Json

## Development

### Project Structure Explanation

- **AppHost**: .NET Aspire orchestrator defining services and local development infrastructure
- **ServiceDefaults**: Shared service configuration, telemetry, and extension methods
- **Silo**: Orleans silo host that runs the grain implementations
- **Grains**: Orleans grain interfaces and implementations:
  - Rule storage and Prolog execution
  - Application evaluation state
  - Mock RAG context provider

### Testing

Run unit and integration tests:
```bash
dotnet test
```

### Building & Deployment

Build release:
```bash
dotnet build -c Release
```

The system is designed to run in containerized environments with support for:
- Docker container deployment
- Azure Container Instances
- Kubernetes (with Orleans Kubernetes hosting)

## Key Technologies

- **Orleans**: Distributed virtual actor framework
- **DotProlog**: Prolog language runtime for .NET
- **Claude API**: Large language model for schema deduction and fact extraction
- **Azure Table Storage**: Persisted grain state storage
- **Aspire**: .NET orchestration platform

## References

- [DotProlog Documentation](dotprolog/README.md)
- [Orleans Documentation](https://learn.microsoft.com/en-us/dotnet/orleans/)
- [.NET Aspire Documentation](https://learn.microsoft.com/en-us/dotnet/aspire/)
- [Claude API Documentation](https://docs.anthropic.com/)

## Contributing

This project is under active development. Please see CONTRIBUTING.md for guidelines.

## License

[Add your license here]

## Author

Bjarte Aarmo Lund
