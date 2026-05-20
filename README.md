# Compilator

Online judge (code evaluation) service built with ASP.NET Core 8. Accepts code submissions in multiple languages, compiles them, runs against test cases, and returns a verdict.

## Supported languages

| Language | Compiler/Runtime | Notes |
|----------|-----------------|-------|
| C++ | g++ -O2 | Native binary |
| C | gcc -O2 | Native binary |
| Java | javac + java | JVM |
| Python 3 | python3 | Interpreted |
| C# | Roslyn (in-process) | No external compiler needed |
| Go | go build | Native binary |

## API endpoints

### `POST /api/judge/submit`

Submit code for evaluation.

**JSON body:**
```json
{
  "problem": "A",
  "language": "Cpp",
  "code": "#include <iostream>\nusing namespace std;\nint main(){ ... }",
  "timeLimitMs": 2000,
  "memoryLimitMb": 256
}
```

**Form-data (file upload):**
```
problem=A
language=Cpp
timeLimitMs=2000
memoryLimitMb=256
file=<source file>
```

**Response:**
```json
{
  "finalVerdict": "Accepted",
  "passedTests": 15,
  "totalTests": 15,
  "testResults": [
    { "testNumber": 1, "verdict": "Accepted", "timeMs": 12, "memoryKb": 1024 }
  ],
  "compilationError": null
}
```

Possible verdicts: `Accepted`, `WrongAnswer`, `TimeLimitExceeded`, `MemoryLimitExceeded`, `RuntimeError`, `CompilationError`, `InternalError`.

Limits: code ≤ 200 KB, `timeLimitMs` 100–30000, `memoryLimitMb` 16–1024.

---

### `GET /api/admin/problems`

Returns all available problems with test case counts.

```json
[
  { "problemId": "A", "testCount": 15 },
  { "problemId": "B", "testCount": 20 }
]
```

### `GET /api/admin/problems/{problemId}/test-count`

Returns the number of test cases for a specific problem.

---

## Test case structure

Test cases are read from the path configured in `appsettings.json` (`Judge:TestCasesBasePath`, default `/opt/judge/testcases`).

```
testcases/
  A/
    0001.in
    0001.out
    0002.in
    0002.out
    ...
  B/
    0001.in
    0001.out
    ...
```

## Configuration

`appsettings.json`:
```json
{
  "Judge": {
    "TestCasesBasePath": "/opt/judge/testcases",
    "MaxParallelContainers": 4
  }
}
```

`MaxParallelContainers` limits how many submissions run concurrently.

## Running locally

**Prerequisites:** .NET 8 SDK, g++, gcc, java/javac, python3, go.

```bash
cd src
dotnet run
```

Swagger UI is available at `http://localhost:5094`.

## Running with Docker

```bash
docker build -f src/Dockerfile -t compilator .
docker run -p 5094:8080 \
  -v $(pwd)/testcases:/opt/judge/testcases:ro \
  compilator
```

## Running with Docker Compose

```bash
docker compose up
```

See [docker-compose.yml](../docker-compose.yml) for full configuration.
