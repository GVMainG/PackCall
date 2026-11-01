from pathlib import Path
import json,re,shutil
root=Path.cwd(); e=root/'tests/reorganization-evidence'; project=root/'PackCall.Test'
assert not (root/'PackCall.Core.Test/PackCall.Core.Test.csproj').exists()
(e/'old-project-removal.json').write_text(json.dumps({'sources_removed_via_apply_patch':True,'remaining_old_directory':'ignored bin/obj build outputs only','organization_started_after_source_removal':True},indent=2))
modules={'ApiControllerTests':'Api','CampaignServiceTests':'Campaigns','CampaignTests':'Campaigns','CampaignWorkflowTests':'Campaigns','DeliveryMeasurementTests':'Deliveries','DeliveryProcessingServiceTests':'Deliveries','DeliveryWorkerTests':'Workers','MeasurementCollectorTests':'Measurements','MeasurementWriterTests':'Measurements','OperatorHttpTests':'Operators','TestOperatorTests':'Operators','WorkerLeaseBudgetTests':'Workers','WorkerMeasurementTests':'Workers','CampaignPausePostgresTests':'Campaigns','WorkerPostgresTests':'Workers','DeliveryResultLeasePostgresTests':'Deliveries','OperatorPostgresTests':'Operators','PerformancePostgresTests':'Measurements'}
postgres=['CampaignPausePostgresTests','WorkerPostgresTests','DeliveryResultLeasePostgresTests','OperatorPostgresTests','PerformancePostgresTests']
def save(p,s):p.parent.mkdir(parents=True,exist_ok=True);p.write_text(s,encoding='utf-8',newline='')
campaign=(project/'CampaignPausePostgresTests.cs').read_text(encoding='utf-8');leases=(project/'DeliveryResultLeasePostgresTests.cs').read_text(encoding='utf-8')
start=campaign.index('    private readonly');end=campaign.index('    [PostgresFact]')
common=campaign[start:end]; ls=leases.index('    private static readonly');le=leases.index('    [PostgresTheory]')
common+=leases[ls:le]
common=common.replace('    private ', '    private protected ')
headers='\n'.join(dict.fromkeys(re.findall(r'^using .*;',campaign+'\n'+leases,re.M)))
save(project/'Integration/Support/PostgresTestBase.cs',headers+'\n\nnamespace PackCall.Core.Test;\n\npublic abstract class PostgresTestBase : IAsyncLifetime\n{\n'+common+'}\n')
campaign=campaign[:start]+campaign[end:];leases=leases[:ls]+leases[le:]
save(project/'CampaignPausePostgresTests.cs',campaign);save(project/'DeliveryResultLeasePostgresTests.cs',leases)
mapping={}
for stem,module in modules.items():
 p=project/(stem+'.cs');s=p.read_text(encoding='utf-8');category='Integration' if stem in postgres or stem in ['OperatorHttpTests','MeasurementWriterTests'] else 'Unit'
 if stem in postgres:
  s=s.replace('public partial class CampaignPausePostgresTests : IAsyncLifetime','public class CampaignPausePostgresTests : PostgresTestBase') if stem==postgres[0] else s.replace('public partial class CampaignPausePostgresTests','public class '+stem+' : PostgresTestBase')
  if stem!=postgres[0]:s=s.replace('public class '+stem,'[Collection("Measurement")]\npublic class '+stem,1)
 s=re.sub(r'(public (?:sealed )?class '+stem+r'\b)',f'[Trait("Category", "{category}")]\n[Trait("Module", "{module}")]\n'+r'\1',s,count=1)
 target=project/category/module/p.name;save(target,s);p.unlink();mapping['PackCall.Core.Test/'+p.name]=target.relative_to(root).as_posix()
for name in ['Fakes.cs','DeliveryTestData.cs']:
 target=project/'Support'/name;target.parent.mkdir(exist_ok=True);shutil.move(str(project/name),target);mapping['PackCall.Core.Test/'+name]=target.relative_to(root).as_posix()
# Separate HTTP fixture from its scenario class, retaining every implementation line.
p=project/'Integration/Operators/OperatorHttpTests.cs';s=p.read_text(encoding='utf-8');i=s.index('internal sealed class OperatorServer');fixture=s[i:];save(project/'Integration/Support/OperatorServer.cs',s[:s.index('namespace ')]+ 'namespace PackCall.Core.Test;\n\n'+fixture);save(p,s[:i])
(e/'csharp-source-mapping.json').write_text(json.dumps(mapping,indent=2))
