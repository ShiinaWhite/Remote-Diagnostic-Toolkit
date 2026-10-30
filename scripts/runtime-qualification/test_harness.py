import hashlib
import json
import pathlib
import tempfile
import unittest
import urllib.error
import urllib.request
from unittest.mock import patch

from fixture_provider import FixtureProvider, FIXTURE_KEY, SENTINEL
from qualify import evaluate, profile_metadata, verify_identity


class QualificationHarnessTests(unittest.TestCase):
    def test_missing_or_wrong_runtime_hash_fails_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            candidate = pathlib.Path(folder) / "zcode-windows-x64.exe"
            candidate.write_bytes(b"synthetic-not-a-runtime")
            identity = {"qualification_only": True, "production_enabled": False,
                        "upstream_commit": "a" * 40, "runtime_filename": candidate.name}
            for digest in (None, "latest", "0" * 64):
                with self.subTest(digest=digest), self.assertRaises(ValueError):
                    verify_identity(candidate, dict(identity, runtime_sha256=digest))
            verify_identity(candidate, dict(identity, runtime_sha256=hashlib.sha256(candidate.read_bytes()).hexdigest()))

    def test_production_or_arbitrary_executable_identity_is_rejected(self):
        for identity in ({"production_enabled": True}, {"qualification_only": False}):
            with self.assertRaises(ValueError):
                verify_identity(pathlib.Path("cmd.exe"), identity)

    @staticmethod
    def result():
        return {"workspace_before": {"fixture.txt": "hash"}, "workspace_after": {"fixture.txt": "hash"},
                "cleanup": "Complete", "session_absent": True, "exit_code": 0}

    def test_exit_zero_does_not_prove_denial(self):
        self.assertEqual("NOT_PROVEN", evaluate("write", self.result(), [], "")['status'])

    def test_doctor_must_prove_sea_and_pinned_version(self):
        doctor = {'cli': {'version': '0.17.0'}, 'runtime': {'sea': False,
                  'platform': 'win32', 'arch': 'x64', 'node': 'v24.14.0'}}
        self.assertEqual('FAIL', evaluate('doctor', self.result(), [], json.dumps(doctor))['status'])
        doctor['runtime']['sea'] = True
        self.assertEqual('PASS', evaluate('doctor', self.result(), [], json.dumps(doctor))['status'])

    def test_side_effect_overrides_denial_text(self):
        result = self.result()
        result["workspace_after"] = {"WRITE_MUST_NOT_EXIST.txt": "hash"}
        request = {"body": {"messages": [{"role": "tool", "content": "Tool denied"}]}}
        self.assertEqual("FAIL", evaluate("write", result, [request], "")['status'])

    def test_cleanup_failure_cannot_pass(self):
        result = self.result()
        result['cleanup'] = 'Failed'
        self.assertEqual("FAIL", evaluate("version", result, [], "0.17.0")['status'])

    def test_reads_require_sentinel_in_actual_tool_result(self):
        self.assertEqual("NOT_PROVEN", evaluate("read", self.result(), [], SENTINEL)['status'])
        request = {"body": {"messages": [{"role": "tool", "content": SENTINEL}]}}
        self.assertEqual("PASS", evaluate("read", self.result(), [request], "")['status'])

    def test_loopback_fixture_rejects_wrong_auth_and_never_forwards(self):
        with tempfile.TemporaryDirectory() as folder, FixtureProvider(pathlib.Path(folder)) as fixture:
            request = urllib.request.Request(fixture.endpoint + '/chat/completions', b'{}',
                                             {'Authorization': 'Bearer fixture-wrong'})
            with self.assertRaises(urllib.error.HTTPError) as error:
                urllib.request.urlopen(request)
            self.assertEqual(401, error.exception.code)
            self.assertEqual([], fixture.requests)

    def test_fixture_sends_tool_call_then_final_response(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            (root/'read').mkdir()
            (root/'read/launch.json').write_text(json.dumps({'fixture_file': str(root/'workspace/fixture.txt')}))
            with FixtureProvider(root) as fixture:
                def post(messages):
                    request = urllib.request.Request(fixture.endpoint + '/chat/completions',
                        json.dumps({'model': 'rdt-case-read', 'messages': messages}).encode(),
                        {'Authorization': 'Bearer ' + FIXTURE_KEY, 'Content-Type': 'application/json'})
                    return json.load(urllib.request.urlopen(request))
                first = post([])
                self.assertEqual('Read', first['choices'][0]['message']['tool_calls'][0]['function']['name'])
                final = post([{'role': 'tool', 'content': SENTINEL}])
                self.assertEqual('stop', final['choices'][0]['finish_reason'])

    def test_real_profile_snapshot_does_not_open_file_contents(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            (root/'credential-fixture.bin').write_bytes(b'not-a-real-credential')
            with patch.object(pathlib.Path, 'open', side_effect=AssertionError('content read forbidden')):
                metadata = profile_metadata(root)
            self.assertEqual([], metadata['errors'])
            self.assertIn('credential-fixture.bin', metadata['entries'])

    def test_public_export_omits_raw_paths_pids_and_model_requests(self):
        import summarize
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            case = root/'write'
            case.mkdir()
            identity = root/'identity.json'
            identity.write_text(json.dumps({'runtime_sha256': '0'*64}))
            result = dict(self.result(), runtime_sha256='0'*64, observation={
                'file_events': [{'root':'USERPROFILE','kind':'changed','relative_path':'PRIVATE_CANARY_PATH'}],
                'dropped_events': 0, 'observed_processes':[{'pid':123456789,'name':'candidate'}],
                'tcp_ipv4': []})
            (case/'result.json').write_text(json.dumps(result))
            (case/'provider-requests.json').write_text('[]')
            (case/'stdout.txt').write_text('PRIVATE_MODEL_PROMPT_CANARY')
            with patch.object(summarize, 'RUNS', root), patch.object(summarize, 'CASES', ['write']), patch.object(summarize, 'IDENTITY', identity):
                public = json.dumps(summarize.export())
            for private in ['PRIVATE_CANARY_PATH','123456789','PRIVATE_MODEL_PROMPT_CANARY']:
                self.assertNotIn(private, public)


if __name__ == '__main__':
    unittest.main()
