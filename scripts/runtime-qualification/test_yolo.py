"""Evidence evaluators must not turn exit code, fixtures, or recovery into a PASS."""
import copy
import hashlib
import json
import pathlib
import unittest

from qualify_yolo import evaluate, powershell_results
from yolo_fixture import calls, script


class EvidenceTests(unittest.TestCase):
    def setUp(self):
        self.result = {'cleanup':'Complete','session_absent':True,'registry_absent_final':True,
            'registry_absent_before_recovery':True,'children':{'recovery_required':False,'markers':[]},
            'runtime_exit':0,'errors':[],'forced_kill':False,'protocol_result':{'completed':True,
                'initial_state':{'result':{'settings':{'permission':{'mode':'yolo'}}}}}}

    def request(self, content):
        return [{'body':{'tools':[{'function':{'name':'Bash'}}],
            'messages':[{'role':'tool','tool_call_id':'one','content':content}]}}]

    def test_zero_exit_is_not_powershell_evidence(self):
        self.assertEqual(evaluate('services',self.result,[],{},{})['status'],'NOT_PROVEN')

    def test_tool_surface_cannot_prove_requested_permission_mode(self):
        self.result['protocol_result']['initial_state']['result']['settings']['permission']['mode']='plan'
        self.assertEqual(evaluate('surface',self.result,self.request(''),{},{})['status'],'NOT_PROVEN')

    def test_privilege_query_needs_native_exit_and_output(self):
        value = {'case':'privilege','ok':True,'data':[{'administrator':True,'query_exit':None,'query_output':''}]}
        self.assertEqual(evaluate('privilege',self.result,self.request('RDT_YOLO_RESULT:'+json.dumps(value)),{},{})['status'],'NOT_PROVEN')

    def test_wrapper_cancel_needs_actual_wrapper_cancellation_signal(self):
        self.result['children']['markers'] = ['active.pid.json']
        self.result['protocol_result'] = {'target_observed':True,'stop_ack':True}
        self.assertEqual(evaluate('wrapper-cancel',self.result,self.request(''),{},{})['status'],'NOT_PROVEN')

    def test_error_output_cannot_pass(self):
        request = self.request('RDT_YOLO_RESULT:'+json.dumps({'case':'services','ok':False,'error':'Access denied'}))
        self.assertEqual(evaluate('services',self.result,request,{},{})['status'],'NOT_PROVEN')

    def test_real_tool_result_and_rows_required(self):
        value = {'case':'services','ok':True,'count':1,'data':[{'Name':'synthetic-test-only'}]}
        self.assertEqual(evaluate('services',self.result,self.request('RDT_YOLO_RESULT:'+json.dumps(value)),{},{})['status'],'PASS')
        value['data'] = []
        self.assertEqual(evaluate('services',self.result,self.request('RDT_YOLO_RESULT:'+json.dumps(value)),{},{})['status'],'NOT_PROVEN')

    def test_success_text_without_file_hash_cannot_pass(self):
        self.assertEqual(evaluate('write',self.result,self.request('File created successfully'),{},{})['status'],'NOT_PROVEN')

    def test_real_write_hash_and_tool_result(self):
        after = {'written.txt':hashlib.sha256(b'RDT_WRITTEN_SENTINEL\n').hexdigest()}
        self.assertEqual(evaluate('write',self.result,self.request('File created successfully'),{},after)['status'],'PASS')

    def test_recovered_orphan_still_fails(self):
        self.result['children']['recovery_required'] = True
        self.result['children']['recovered'] = True
        self.assertEqual(evaluate('surface',self.result,self.request(''),{},{})['status'],'FAIL')

    def test_registry_fallback_cleanup_not_tool_success(self):
        value = {'case':'registry-lifecycle','ok':True,'data':[{'created_read':'RDT_CREATED','modified_read':'RDT_MODIFIED','deleted':True}]}
        self.result['registry_absent_before_recovery'] = False
        self.assertEqual(evaluate('registry-lifecycle',self.result,self.request('RDT_YOLO_RESULT:'+json.dumps(value)),{},{})['status'],'FAIL')

    def test_loaded_dotenv_canary_fails(self):
        value = {'case':'parent-dotenv','ok':True,'data':[{'canary':'SHOULD_NOT_LOAD','base_url':None}]}
        self.assertEqual(evaluate('parent-dotenv',self.result,self.request('RDT_YOLO_RESULT:'+json.dumps(value)),{},{})['status'],'FAIL')

    def test_parser_reads_tool_json_without_inventing_output(self):
        self.assertEqual(powershell_results(['RDT_YOLO_RESULT:not-json']),[])
        self.assertEqual(powershell_results(['stdout\nRDT_YOLO_RESULT:{"ok":true}\n']),[{'ok':True}])

    def test_repeated_tool_transcript_is_deduplicated(self):
        from qualify_yolo import outputs
        request = self.request('actual-output')
        self.assertEqual(outputs(request+copy.deepcopy(request)),['actual-output'])

    def test_builtin_pin_is_official_exact_source_not_empty_config(self):
        pin = json.loads(pathlib.Path(__file__).with_name('yolo-runtime-identity.json').read_text())
        self.assertEqual(pin['builtin_source_path'],'config/provider/zcode-builtin.test.json')
        self.assertEqual(len(pin['builtin_sha256']),64)
        self.assertFalse(pin['production_enabled'])

    def test_fixture_only_generates_owned_registry_path_and_workspace(self):
        launch = {'workspace':r'C:\Temp\RDT-123\workspace','session_id':'123',
                  'registry_path':r'Software\RdtQualification-123','powershell':r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'}
        generated = script('registry-lifecycle',launch)
        self.assertIn(r'HKCU:\Software\RdtQualification-123',generated)
        self.assertNotIn('HKLM',generated)
        self.assertNotIn('ExecutionPolicy',generated)
        self.assertEqual(calls('write',launch)[0][0],'Write')


if __name__ == '__main__':
    unittest.main()
