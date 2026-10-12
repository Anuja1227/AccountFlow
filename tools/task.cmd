echo ===== BUILD =====
dotnet build Backend\AccountOpeningTracker.Api\AccountOpeningTracker.Api.csproj -nologo -v:minimal -clp:NoSummary
echo ===== TESTS =====
dotnet test Backend\AccountOpeningTracker.Tests\AccountOpeningTracker.Tests.csproj -nologo -v:minimal
echo ===== FRONTEND BUILD =====
pushd Frontend
call npm run build
popd
