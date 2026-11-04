from concurrent.futures import ThreadPoolExecutor

from backend.auth import LoginThrottle
from backend.live import LiveEngine
from backend.topology import topology_from_dict
from conftest import new_store
from test_api_rbac import client, make_admin


def test_five_wrong_passwords_block_that_device_even_with_the_right_one(base_cfg):
    c = client(LiveEngine(new_store(), topology_from_dict(base_cfg)))
    make_admin(c, "root", "rootpass1")
    for _ in range(5):
        assert c.post("/auth/login", json={"username": "root", "password": "wrong"}).status_code == 401
    assert c.post("/auth/login", json={"username": "ROOT ", "password": "rootpass1"}).status_code == 429
    assert c.post("/auth/login", json={"username": "other", "password": "x"}).status_code == 401


def test_a_guesser_on_one_device_does_not_lock_the_user_out_of_another():
    t = LoginThrottle(per_user=3, per_address=100, window_s=60, clock=lambda: 1000.0)
    for _ in range(3):
        assert t.attempt("asha", "10.0.0.5")
    assert not t.attempt("asha", "10.0.0.5")
    assert t.attempt("asha", "10.0.0.9")


def test_the_block_ends_with_the_window_and_a_right_password_clears_the_count():
    now = [1000.0]
    t = LoginThrottle(per_user=3, per_address=100, window_s=60, clock=lambda: now[0])
    for _ in range(2):
        t.attempt("asha", "10.0.0.5")
    t.attempt("asha", "10.0.0.5")
    t.succeeded("asha", "10.0.0.5")
    assert t.attempt("asha", "10.0.0.5") and t.attempt("asha", "10.0.0.5") and t.attempt("asha", "10.0.0.5")
    assert not t.attempt("asha", "10.0.0.5")
    now[0] += 61
    assert t.attempt("asha", "10.0.0.5")


def test_one_address_guessing_many_names_is_blocked_and_ipv6_counts_by_its_64():
    t = LoginThrottle(per_user=5, per_address=20, window_s=900)
    for i in range(20):
        assert t.attempt(f"user{i}", "10.0.0.7")
    assert not t.attempt("someone-new", "10.0.0.7") and t.attempt("someone-new", "10.0.0.8")
    for i in range(20):
        assert t.attempt(f"v6-{i}", f"2001:db8:1:2::{i + 1:x}")
    assert not t.attempt("someone-else", "2001:db8:1:2:ffff::1")
    assert t.attempt("someone-else", "2001:db8:1:3::1")


def test_parallel_wrong_passwords_cannot_slip_past_the_limit():
    t = LoginThrottle(per_user=5, per_address=1000, window_s=900)
    with ThreadPoolExecutor(30) as pool:
        allowed = sum(pool.map(lambda _: t.attempt("root", "10.0.0.5"), range(30)))
    assert allowed == 5


def test_a_full_memory_forgets_the_idlest_keys_not_every_block():
    now = [1000.0]
    t = LoginThrottle(per_user=2, per_address=10_000, window_s=900, keep=50, clock=lambda: now[0])
    t.attempt("admin", "10.0.0.5")
    t.attempt("admin", "10.0.0.5")
    for i in range(500):
        now[0] += 0.01
        t.attempt(f"spray{i}", f"10.1.{i // 250}.{i % 250}")
    assert len(t._fails) <= 50
    assert t._fails, "memory is bounded by forgetting some keys, not all"
