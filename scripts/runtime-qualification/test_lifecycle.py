"""Reject misleading lifecycle/Unicode proofs, without executing a candidate or API."""
import copy
import hashlib
import json
import unittest

from lifecycle_fixture import STRINGS, UNICODE_PATH, PREAMBLE, calls
from qualify_lifecycle import evaluate


class LifecycleProofTests(unittest.TestCase):
    def setUp(self):
        self.result={'created_job':True,'assigned':True,'root_member':True,'limit_flags':0x2000,'handle_inheritable':False,
            'cleanup':'Complete','session_absent':True,'after_close':{'all_exited':True},'recovery':{'recovery_required':False},
            'startup_exited_without_recovery':True,'startup_processes':[],'errors':[],'runtime_exit':0,
            'memberships':{'active.pid.json':{'member':True,'pid':101},'detached.pid.json':{'member':True,'pid':102},'hold.pid.json':{'member':True,'pid':103}},
            'live_before_close':[{'pid':102}],
            'protocol_result':{'completed':True,'stop_ack':True,'wrapper_token_cancelled':True,
                'initial_state':{'result':{'settings':{'permission':{'mode':'yolo'}}}}}}

    def request(self,content):
        return [{'body':{'tools':[{'function':{'name':n}} for n in ['Bash','Read','Write','Edit']],
            'messages':[{'role':'tool','tool_call_id':'first','content':content}]}}]

    def unicode(self,version='5.1.0'):
        data={'strings':STRINGS,'json':{'strings':STRINGS},'file':'\n'.join(STRINGS),
            'relative_path':UNICODE_PATH,'leaf':'中文诊断.txt','version':version}
        content='\n'.join('RDT_UNICODE_DIRECT:'+s for s in STRINGS)+'\nRDT_UNICODE_JSON:'+json.dumps({'strings':STRINGS})
        content+='\nRDT_YOLO_RESULT:'+json.dumps({'case':'unicode-ps51','ok':True,'data':[data]})
        requests=self.request(content)
        requests[0]['body']['messages'].append({'role':'tool','tool_call_id':'read','content':'\n'.join(STRINGS)})
        workspace={UNICODE_PATH.replace('/','\\'):hashlib.sha256('\n'.join(STRINGS).encode()).hexdigest()}
        return requests,workspace

    def test_detached_success_requires_kernel_and_membership(self):
        requests=self.request('RDT_YOLO_RESULT:'+json.dumps({'case':'detached-child','ok':True,'data':[{}]}))
        self.assertEqual(evaluate('detached-child',self.result,requests,{})['status'],'PASS')
        self.result['memberships']['detached.pid.json']['member']=False
        self.assertEqual(evaluate('detached-child',self.result,requests,{})['status'],'FAIL')

    def test_disappearance_without_job_binding_fails(self):
        self.result['assigned']=False
        self.assertEqual(evaluate('normal',self.result,self.request(''),{})['status'],'FAIL')

    def test_recovery_never_counts_as_job_success(self):
        self.result['recovery']['recovery_required']=True
        self.result['recovery']['recovered']=True
        self.assertEqual(evaluate('startup-race',self.result,[],{})['status'],'FAIL')

    def test_breakaway_flags_fail(self):
        for flag in [0x800,0x1000]:
            self.result['limit_flags']=0x2000|flag
            self.assertFalse(evaluate('normal',self.result,self.request(''),{})['checks']['job_bound'])

    def test_inheritable_handle_fails(self):
        self.result['handle_inheritable']=True
        self.assertFalse(evaluate('normal',self.result,self.request(''),{})['checks']['job_bound'])

    def test_injection_must_prevent_all_protocol_requests(self):
        result={'assigned':False,'protocol_requests':0,'errors':['INJECTED_JOB_ASSIGN_FAILURE'],
                'cleanup':'Complete','session_absent':True,'unassigned_stopped':True}
        self.assertEqual(evaluate('inject-assign-failure',result,[],{})['status'],'PASS')
        result['protocol_requests']=1
        self.assertEqual(evaluate('inject-assign-failure',result,[],{})['status'],'FAIL')

    def test_unknown_preassign_child_is_not_excused_by_exit(self):
        self.result['startup_processes']=[{'windows_console_host':False,'member_after_root_assign':False}]
        self.assertFalse(evaluate('normal',self.result,self.request(''),{})['checks']['known_preassign_processes_only'])

    def test_detached_must_be_alive_before_job_close(self):
        self.result['live_before_close']=[]
        self.assertFalse(evaluate('detached-child',self.result,self.request(''),{})['checks']['detached_alive_before_job_close'])

    def test_unicode_exact_positive(self):
        requests,workspace=self.unicode()
        self.assertEqual(evaluate('unicode-ps51',self.result,requests,workspace)['status'],'PASS')

    def test_replacement_character_fails(self):
        requests,workspace=self.unicode()
        requests[0]['body']['messages'][0]['content']+='\n\ufffd'
        self.assertEqual(evaluate('unicode-ps51',self.result,requests,workspace)['status'],'FAIL')

    def test_direct_unicode_extra_character_fails(self):
        requests,workspace=self.unicode()
        requests[0]['body']['messages'][0]['content']=requests[0]['body']['messages'][0]['content'].replace('RDT_UNICODE_DIRECT:'+STRINGS[0], 'RDT_UNICODE_DIRECT:'+STRINGS[0]+'x')
        self.assertFalse(evaluate('unicode-ps51',self.result,requests,workspace)['checks']['unicode_powershell'])

    def test_file_hash_cannot_be_replaced_by_echo(self):
        requests,workspace=self.unicode(); workspace[next(iter(workspace))]='bad-hash'
        self.assertFalse(evaluate('unicode-ps51',self.result,requests,workspace)['checks']['unicode_file'])

    def test_builtin_read_must_really_return_unicode(self):
        requests,workspace=self.unicode(); requests[0]['body']['messages'].pop()
        self.assertFalse(evaluate('unicode-ps51',self.result,requests,workspace)['checks']['builtin_read_unicode'])

    def test_readonly_surface_cannot_substitute_for_yolo(self):
        requests,workspace=self.unicode(); requests[0]['body']['tools']=[{'function':{'name':'Read'}}]
        self.assertFalse(evaluate('unicode-ps51',self.result,requests,workspace)['checks']['normal_core_surface'])

    def test_fixture_preamble_and_command_budget(self):
        launch={'workspace':r'C:\Temp\RDT-123\workspace','session_id':'123','powershell':r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
                'powershell7':r'C:\Program Files\PowerShell\7\pwsh.exe'}
        import base64
        for name in ['unicode-ps51','unicode-ps7','detached-child','runtime-error']:
            command=calls(name,launch)[0][1]['command']
            script=base64.b64decode(command.split('-EncodedCommand ')[1]).decode('utf-16le')
            self.assertTrue(script.startswith(PREAMBLE))
            self.assertLess(len(command),7800)
            self.assertNotIn('ExecutionPolicy',command)


if __name__=='__main__': unittest.main()
