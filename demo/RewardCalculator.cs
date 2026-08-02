using System.Collections.Generic;

namespace CardRewards;

/// <summary>카드 한 건의 거래 내역.</summary>
public record Transaction(string Merchant, decimal Amount);

/// <summary>
/// 이번 달 카드 사용 내역으로 캐시백(적립)을 계산한다.
/// 총 사용액에 따라 등급(Basic/Gold/VIP)을 정하고, 건당 평균이 큰 우수 고객에게 추가 적립을 준다.
/// </summary>
public static class RewardCalculator
{
    private const decimal BasicRate = 0.005m;  // 0.5%
    private const decimal GoldRate  = 0.01m;   // 1.0%
    private const decimal VipRate   = 0.02m;   // 2.0%

    /// <summary>이번 달 캐시백 금액을 계산한다.</summary>
    public static decimal MonthlyCashback(List<Transaction> transactions)
    {
        decimal total = 0m;
        foreach (var t in transactions)
        {
            total += t.Amount;
        }

        // 건당 평균 사용액 — 건당 평균이 큰 '알짜 고객'을 우대하는 데 쓴다.
        decimal avgPerTransaction = total / transactions.Count;

        decimal rate = TierRate(total);
        if (avgPerTransaction >= 200_000m)
        {
            rate *= 1.1m;  // 건당 평균 20만원 이상이면 적립 10% 추가
        }

        return total * rate;
    }

    /// <summary>이번 달 총 사용액으로 등급별 적립률을 정한다.</summary>
    public static decimal TierRate(decimal monthlySpend)
    {
        if (monthlySpend > 3_000_000m)
        {
            return VipRate;   // 300만원 초과 → VIP
        }

        if (monthlySpend > 1_000_000m)
        {
            return GoldRate;  // 100만원 초과 → Gold
        }

        return BasicRate;
    }
}
