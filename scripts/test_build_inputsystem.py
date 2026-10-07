"""Self-check for the asmdef versionDefines evaluator: python3 -I scripts/test_build_inputsystem.py"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_inputsystem as B

U = '2022.3.62f2'
for expr, want in [('2022.2', True), ('2022.3', True), ('1', True), ('6000.0.9', False), ('6000.1.0b9', False),
                   ('[2021.3.11,2022.1)', False), ('[2022.1.19,2022.2)', False), ('[2022.3,2023.1)', True),
                   ('(2022.3.62f2,2023.1)', False), ('[2022.3.62f2]', True), ('[2022.3.61f1]', False)]:
    assert B.in_range(U, expr) == want, expr
asmdef = {'versionDefines': [{'name': 'com.unity.modules.vr', 'expression': '1.0.0', 'define': 'VR'},
                             {'name': 'com.unity.xr.oculus', 'expression': '1.0.3', 'define': 'NO_OCULUS'},
                             {'name': 'Unity', 'expression': '[2022.1.19,2022.2)', 'define': 'HAS'},
                             {'name': 'Unity', 'expression': '2022.2', 'define': 'HAS'}]}
assert B.version_defines(asmdef, {'Unity': U, 'com.unity.modules.vr': '1.0.0'}) == ['HAS', 'VR']
print('ok')
