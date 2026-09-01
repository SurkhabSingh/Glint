param(
    [switch]$SkipInference
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build .\Glint.Windows.Phase0.sln --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed." }
    dotnet test .\Glint.Windows.Phase0.sln --configuration Debug --no-build
    if ($LASTEXITCODE -ne 0) { throw "Test suite failed." }
    dotnet run --project .\src\Glint.Phase0.Cli --configuration Debug --no-build -- `
        compatibility --require-ready
    if ($LASTEXITCODE -ne 0) { throw "Compatibility verification failed." }
    dotnet run --project .\src\Glint.Phase0.Cli --configuration Debug --no-build -- `
        storage --data-dir .phase0-verification
    if ($LASTEXITCODE -ne 0) { throw "Storage verification failed." }
    dotnet run --project .\src\Glint.Phase0.Cli --configuration Debug --no-build -- `
        vector-smoke --data-dir .phase0-verification
    if ($LASTEXITCODE -ne 0) { throw "Vector verification failed." }

    if (-not $SkipInference) {
        $python = ".\tools\litert\.venv\Scripts\python.exe"
        $worker = ".\tools\litert\worker.py"
        $gemma = ".\models\upstream\gemma-4-e2b\gemma-4-E2B-it.litertlm"
        $embedModel = ".\models\upstream\nomic-embed-text-v1.5\onnx\model_int8.onnx"
        $vocab = ".\models\upstream\nomic-embed-text-v1.5\vocab.txt"

        dotnet run --project .\src\Glint.Phase0.Cli --configuration Debug --no-build -- `
            model-generate --python $python --worker $worker --model $gemma `
            --backend cpu --prompt "Reply with exactly GLINT_VERIFY_OK and nothing else."
        if ($LASTEXITCODE -ne 0) { throw "Gemma verification failed." }
        dotnet run --project .\src\Glint.Phase0.Cli --configuration Debug --no-build -- `
            activity-summarize `
            --process "Discord" `
            --title "Project chat" `
            --text "Alex reported that deployment is blocked by a login issue and requested logs tonight. The user committed to sending them before a meeting at 10pm tomorrow."
        if ($LASTEXITCODE -ne 0) { throw "Activity summary verification failed." }
        dotnet run --project .\src\Glint.Phase0.EmbeddingProbe `
            --configuration Debug --no-build -- `
            --model $embedModel --vocab $vocab `
            --text "search_document: Glint Phase 0 verification"
        if ($LASTEXITCODE -ne 0) { throw "Embedding verification failed." }
    }
}
finally {
    Pop-Location
}
