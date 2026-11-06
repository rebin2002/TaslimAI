#!/usr/bin/env python3
"""Offline cross-service v2 HMAC fixture for TaslimAI #345/#718 and Bridge #1/#2.

No external HTTP, credentials or production state. Both owners must independently
pin these test vectors against their *actual* C# and Node implementations. This
script alone does not prove receiver ingress, replay, admission or live refill.
"""
from __future__ import annotations

import hashlib
import hmac
import json
import re

SECRET = b"offline-only-taslim-autopilot-v2-test-key"  # synthetic test key, not a live secret
SOURCE = "manus-bridge"
EVENT_TYPE = "wave.task.completed"
EXTERNAL_ID = "evt-offline-contract-001"
TIMESTAMP = "1800000000"  # fixed synthetic test timestamp; NOT a current-time assertion
BODY = b'{"waveKey":"wave-test","taskId":"task-1","outcome":"succeeded"}'
EXPECTED = {
    "unchanged": "77e9cb9d58f2e6106fda007c630992fe315cfa0dd7208d802e5631396613f042",
    "mutated_source": "bc5f2830d06f6329a902141ca5c3c22a1ece17db93d31882b559a346766484d9",
    "mutated_type": "7604baab0ba8d2de264699e6307daf3ae958582524e6d2a3278733b53ab07d13",
    "mutated_id": "77dc31c2409c2a1e5b41bd24236be71f42731d1baa0a2347371aef56df82fb9b",
    "mutated_timestamp": "77af7642304ec5bc0a29a674dca3808d2ecc79ccf2c9660bfd6a91834597a560",
    "mutated_body": "9045173d30f8ae6794d66920f418618b9311f2fbda57182d5a002351668fb3cf",
}

def canonical_v2_bytes(timestamp: str, source: str, event_type: str,
                       external_id: str, raw_body: bytes) -> bytes:
    if not re.fullmatch(r"[1-9][0-9]{0,11}", timestamp):
        raise ValueError("noncanonical_timestamp")
    for value in (source, event_type, external_id):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,199}", value):
            raise ValueError("invalid_signed_metadata")
    if not isinstance(raw_body, bytes) or not (1 <= len(raw_body) <= 65536):
        raise ValueError("invalid_exact_body")
    digest = hashlib.sha256(raw_body).hexdigest()
    return (
        "taslim-autopilot-v2\n"
        f"{timestamp}\n"
        f"{len(source.encode('utf-8'))}:{source}\n"
        f"{len(event_type.encode('utf-8'))}:{event_type}\n"
        f"{len(external_id.encode('utf-8'))}:{external_id}\n"
        f"{digest}\n"
    ).encode("utf-8")

def signature(timestamp: str, source: str, event_type: str,
              external_id: str, body: bytes) -> str:
    assert len(SECRET) >= 32
    return hmac.new(SECRET,
        canonical_v2_bytes(timestamp, source, event_type, external_id, body),
        hashlib.sha256).hexdigest()

def verify_fixture() -> None:
    vectors = {
        "unchanged": (TIMESTAMP, SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY),
        "mutated_source": (TIMESTAMP, "other-bridge", EVENT_TYPE, EXTERNAL_ID, BODY),
        "mutated_type": (TIMESTAMP, SOURCE, "wave.task.failed", EXTERNAL_ID, BODY),
        "mutated_id": (TIMESTAMP, SOURCE, EVENT_TYPE, "evt-other", BODY),
        "mutated_timestamp": ("1800000001", SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY),
        "mutated_body": (TIMESTAMP, SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY + b" "),
    }
    assert set(vectors) == set(EXPECTED)
    # Pin the exact byte frame, not just a digest. Node and C# must sign the
    # same metadata/sha256 envelope with no implicit CRLF or JSON re-encoding.
    golden_frame = (
        b"taslim-autopilot-v2\n"
        b"1800000000\n"
        b"12:manus-bridge\n"
        b"19:wave.task.completed\n"
        b"24:evt-offline-contract-001\n"
        b"4b4c6edc50d912025d95dc1b9559417970dbd5490c2835987210061798ace3b9\n"
    )
    assert canonical_v2_bytes(TIMESTAMP, SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY) == golden_frame
    # Inclusive bound checks guard cross-runtime off-by-one differences:
    # 12 timestamp digits, 200 ASCII metadata bytes and 65,536 exact body bytes.
    assert canonical_v2_bytes("9" * 12, SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY)
    assert canonical_v2_bytes(TIMESTAMP, "s" * 200, EVENT_TYPE, EXTERNAL_ID, BODY)
    assert canonical_v2_bytes(TIMESTAMP, SOURCE, EVENT_TYPE, EXTERNAL_ID, b"x" * 65536)
    for invalid_timestamp in ("9" * 13, "-1800000000", "1800000000\r\n"):
        try:
            canonical_v2_bytes(invalid_timestamp, SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY)
            raise AssertionError("out-of-bounds or injected timestamp accepted")
        except ValueError:
            pass
    for invalid_metadata in ("s" * 201, "manus-bridge\r\nX-Injected: x", "manus-bridg\u00e9"):
        try:
            canonical_v2_bytes(TIMESTAMP, invalid_metadata, EVENT_TYPE, EXTERNAL_ID, BODY)
            raise AssertionError("out-of-bounds or injected metadata accepted")
        except ValueError:
            pass
    for name, arguments in vectors.items():
        actual = signature(*arguments)
        assert hmac.compare_digest(actual, EXPECTED[name]), (name, actual)
        if name != "unchanged":
            assert not hmac.compare_digest(actual, EXPECTED["unchanged"]), name
    # The legacy v1 MAC authenticates only the timestamp and raw body. Changing
    # source/type/external event ID headers cannot change that MAC. Pin this
    # dangerous difference until the actual Node publisher and C# receiver both
    # require the metadata-bound v2 contract (see #345/#718, Bridge #1/#2).
    legacy_v1 = hmac.new(SECRET,
        (TIMESTAMP + "." + BODY.decode("utf-8")).encode("utf-8"),
        hashlib.sha256).hexdigest()
    assert not hmac.compare_digest(legacy_v1, EXPECTED["unchanged"])
    for changed in (
        (TIMESTAMP, "other-bridge", EVENT_TYPE, EXTERNAL_ID, BODY),
        (TIMESTAMP, SOURCE, "wave.task.failed", EXTERNAL_ID, BODY),
        (TIMESTAMP, SOURCE, EVENT_TYPE, "evt-other", BODY),
    ):
        replayed_v1 = hmac.new(SECRET,
            (changed[0] + "." + changed[4].decode("utf-8")).encode("utf-8"),
            hashlib.sha256).hexdigest()
        assert hmac.compare_digest(replayed_v1, legacy_v1), "unexpected v1 metadata binding"
        assert not hmac.compare_digest(signature(*changed), EXPECTED["unchanged"]), "v2 must bind header identity"
    for invalid_ts in ("01800000000", "+1800000000", "1800000000 ", "0"):
        try:
            canonical_v2_bytes(invalid_ts, SOURCE, EVENT_TYPE, EXTERNAL_ID, BODY)
            raise AssertionError("invalid timestamp accepted")
        except ValueError:
            pass
    for source, kind, event_id in (
        ("", EVENT_TYPE, EXTERNAL_ID),
        ("other bridge", EVENT_TYPE, EXTERNAL_ID),
        (SOURCE, "", EXTERNAL_ID),
        (SOURCE, "event/type", EXTERNAL_ID),
        (SOURCE, EVENT_TYPE, "evt/other"),
        (SOURCE, EVENT_TYPE, EXTERNAL_ID + "\\n"),
    ):
        try:
            canonical_v2_bytes(TIMESTAMP, source, kind, event_id, BODY)
            raise AssertionError("invalid signed identity accepted")
        except ValueError:
            pass
    for invalid_body in (b"", b"x" * 65537):
        try:
            canonical_v2_bytes(TIMESTAMP, SOURCE, EVENT_TYPE, EXTERNAL_ID, invalid_body)
            raise AssertionError("invalid signed body length accepted")
        except ValueError:
            pass
    # Raw-body canonicalization must never turn distinct byte sequences into
    # the same signed event. These pairs decode to equivalent JSON objects but
    # MUST carry different HMACs. Independent Node/C# owners should pin the
    # same golden digests against exact ingress bytes, without JSON re-encoding.
    RAW_BODY_ENCODING_GOLDEN = {
        "reordered": (
            b'{"outcome":"succeeded","taskId":"task-1","waveKey":"wave-test"}',
            "2e352141ebd3b5067b868817b6e7e0ac92f6263e5c45e232ac17bd34f17546d1",
        ),
        "escaped_key": (
            BODY.replace(b'"taskId"', b'"\\u0074askId"'),
            "9d3563ad03dc74f13f3b986f4ee7b1beef315e9e1ee4489d3a7a837a9fe4c2cb",
        ),
        "whitespace": (
            b'{ "waveKey": "wave-test", "taskId": "task-1", "outcome": "succeeded" }',
            "899cf9bf8f77906c7693ac5d6c31480874f54af5ddfab0c7e468c2914f5313d2",
        ),
        "trailing_crlf": (
            BODY + b"\r\n",
            "5187ba82c95ad62d29d0f4a248fa295fb6007a28ede8914c3c57c78272bbd3d3",
        ),
        "unicode": (
            b'{"waveKey":"wave-\xc3\xa9","taskId":"task-1","outcome":"succeeded"}',
            "d2bd912210c69be5d48746b82319666f395b6ca30bb4df9442d9d2aab7d8a2ef",
        ),
        "unicode_escaped": (
            b'{"waveKey":"wave-\\u00e9","taskId":"task-1","outcome":"succeeded"}',
            "63f82ca904d1f2a8e977aaec49c1d333feb836a54640b383d285556421861df8",
        ),
    }
    original_object = json.loads(BODY)
    for name, (raw, golden) in RAW_BODY_ENCODING_GOLDEN.items():
        actual = signature(TIMESTAMP, SOURCE, EVENT_TYPE, EXTERNAL_ID, raw)
        assert hmac.compare_digest(actual, golden), ("raw_body_golden", name, actual)
        assert not hmac.compare_digest(actual, EXPECTED["unchanged"]), ("body_reencoded", name)
        if name not in ("unicode", "unicode_escaped"):
            assert json.loads(raw) == original_object, ("semantic_json_changed", name)
    assert json.loads(RAW_BODY_ENCODING_GOLDEN["unicode"][0]) == json.loads(
        RAW_BODY_ENCODING_GOLDEN["unicode_escaped"][0]
    ), "unicode_json_semantics"
    assert RAW_BODY_ENCODING_GOLDEN["unicode"][1] != RAW_BODY_ENCODING_GOLDEN["unicode_escaped"][1]
    print("PASS: canonical v2 byte frame and bounds, 6 signed-metadata vectors, 6 raw-body byte goldens, 3 v1 replay invariants, rejection cases (offline only)")

if __name__ == "__main__":
    verify_fixture()
