import torch

from sevenwd.losses import compute_losses, masked_log_softmax, policy_loss
from sevenwd.network import Network, NetworkConfig, parameter_count


def test_shapes_and_value_range(manifest):
    net = Network(NetworkConfig.from_manifest(manifest, width=32, blocks=2))
    policy, value, outcome = net(torch.randn(5, manifest.input_size) * 10)
    assert policy.shape == (5, manifest.policy_size)
    assert value.shape == (5,)
    assert outcome.shape == (5, manifest.outcome_size)
    assert value.abs().max() <= 1


def test_default_size_is_about_one_and_a_half_million_parameters(manifest):
    count = parameter_count(Network(NetworkConfig.from_manifest(manifest)))
    assert 1_300_000 < count < 1_700_000


def test_masked_softmax_ignores_illegal_logits():
    legal = torch.tensor([[True, False, True]])
    p = masked_log_softmax(torch.tensor([[1.0, 50.0, 2.0]]), legal).exp()
    assert p[0, 1] == 0
    assert torch.allclose(p.sum(), torch.tensor(1.0))
    other = masked_log_softmax(torch.tensor([[1.0, -50.0, 2.0]]), legal).exp()
    assert torch.allclose(p, other)


def test_policy_loss_is_finite_and_minimal_at_the_target():
    legal = torch.tensor([[True, False, True, True]])
    target = torch.tensor([[0.0, 0.0, 1.0, 0.0]])
    good = policy_loss(torch.tensor([[0.0, 0.0, 30.0, 0.0]]), target, legal)
    bad = policy_loss(torch.tensor([[0.0, 0.0, -30.0, 0.0]]), target, legal)
    assert torch.isfinite(good) and good < 1e-6 < bad


def test_loss_weights():
    legal = torch.ones(1, 2, dtype=torch.bool)
    losses = compute_losses(torch.zeros(1, 2), torch.zeros(1), torch.zeros(1, 4),
                            torch.tensor([[1.0, 0.0]]), legal, torch.ones(1), torch.tensor([0]))
    assert torch.allclose(losses.total, losses.policy + losses.value + 0.25 * losses.outcome)
    assert torch.allclose(losses.value, torch.tensor(1.0))
