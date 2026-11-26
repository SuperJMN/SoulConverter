namespace SoulConverter;

/// <summary>
/// The DPCM samples the demo songs play. They are synthetic: a pitch-swept sine with a click on
/// the front (the kick), band-passed noise over a short shell tone (the snare), high-passed noise
/// (the hat), a plucked 82 Hz bass note with three harmonics, and a one-way ramp like the blip a
/// driver emits when it walks the DAC to mid-scale. Each was delta-modulated the way a DPCM tool
/// does it, taking at every step whichever of the two moves lands closer to the target. Nothing
/// here comes from a game.
/// </summary>
internal static class DemoSamples
{
    public static readonly byte[] Kick = Convert.FromBase64String(
        "1P+ujzCUFIASiPjq/39vrkoRIQghklJr27337rZWlSQRESKRUlWt3e5ut22tKiVJIpFESqmq1tp2225rq1aplJIkSZKUUlXVWlvbtra1VrVSlV" +
        "JSkpJSSqWqqtVqba2ttVarqqqqVEoppZRSqVJVVa1ardZardVqVdVSVZVKlVKpVCpVqaqqqla1arVatVrVqqpVlaoqValSpUpVqlJVVVVV1apa" +
        "1apWtapVVauqqqpUVaWqVKWqVFWqqqqqqqqqqlXVqmpVtaqqVVVVVVVVVaqqUlWVqipVVVWqqqqqqlZVVbWqqlZVVauqqqqqqqqqqqqqSlVVla" +
        "qqqlJVVVVVVVVVVVVVraqqqlZVVVWtqqqqqqqqqqqqUlVVVVWpqqqqqkpVVVVVVVVV1aqqqqqqqlVVVVVVVVW1qqqqqlJVVVVVVVVVlaqqqqqq" +
        "qqqqqqqqqqqqqqqqqqqqqlVVVVVVVVVVVVVVVVVVVVVVVVVVVZWqqqqq6g==");

    public static readonly byte[] Snare = Convert.FromBase64String(
        "/4HPRQCA/z+AAf7/7h8AAEL+D9x/C/CHOACgz/+nXoB/AgC/T4D8/wZdsiEA/n2wfoMZ8CsMDehf/wccB7Dgty/AG978DwDrJEGc//sBvFKMUO" +
        "peE3Hu0wr4NQDy/1wFwf8AqhP9GsLteB5TyIE+9KsR/gVyzDFPYFfpnzKE1xyE+a0G8cseiPVCtF0lG7kvGvIVYM9WxHuqlVSqU8I86zhNkzVm" +
        "KdlVqka3SrVU1jCTrVqlHtlKNWKXWKctZVm1irJaakq6q8pUWZO0SauyVS1plmMVbZUyq1YtqlaVqaqpytxqiqqqqlapqlVVVaWqqqxapapqVZ" +
        "MG");

    public static readonly byte[] Hat = Convert.FromBase64String(
        "xVhLlXLLxVhbbI6mJnU4W8WqqlpN1lTJdKqZi1OpqpmmZpqcnHGylamVprJapmoqm5pWZVpaapWqVqWVmZammmpmZmVVqqVlqWqWpWZVZlaqlu" +
        "o=");

    public static readonly byte[] Bass = Convert.FromBase64String(
        "/f+/3922/y8lSSKRJElSKlWqSlWpUimlpJSUUkqlKlVVqUqVkpIkSZKUlKqqrd297++//37v7dZqlUopSZIkKSmlUpWqqlSVKqVSSimlUqlSVa" +
        "WqKlUqpZSkJCWlUlW1tu3uvfd9773bttWqKqWUlKSklFKpSlVVqapSpVKplEqlUpWqqkpVVapUKimlpJRKVVVrbdvt7t3d2+3WVquqSqWUklJK" +
        "pVKVqqpKVVWqUqVSpVKlqlRVVamqKlWplEoppVKlqlatrW232+1227a2WlVVqlRKqZRKlapUVVVVqqpSlSpVqlKVqqoqVVVVpSpVKpVKpUpVVa" +
        "3W2ta227Zta2u1qqqqUqlUKlWqVFWpqqqqSlWVqlKVqlJVpaqqqqpKVaVKlSqVqlJVtaq11ta2rW1trdWqqqoqVapUqVJVqqqqKlVVVZWqSlWl" +
        "qlJVVVVVqaqqSlWpSpWqUlVVtWq11lpbW2ut1apqVaqqVKVKVamqSlVVVVVVqaoqVVWpqipVVVVVVVWpqlJVqkpVVapVVavWaq211mq1alVVVV" +
        "WpKlWlqkpVVVVVVaWqqqpSVVWqqqqqUlVVVVVVqapSVamqqqqqatVqtVqr1apVraqqqipVlaoqVVVVlaqqqqqqqlJVVZWqqqqqqqqqUlVVVaqq" +
        "UlVVVVVVrWrVqtVq1apaVVVVVZWqqlRVVamqqqqqqqqqSlVVVZWqqqqqqqqqqkpVVZWqqqpS1aqqalWrVq1qVa2qqqqqqqpSVVWlqqqqqqqqqq" +
        "pKVVVVVZWqqqqqqqqqqqpSVVVVqqqqqqpVVatqVatqVVWrqqqqVFVVlaqqqqqqqqqqqlJVVVVVVVWqqqqqqqqqqqqqVFVVVVVVVVVVq6pV1aqq" +
        "VVVVVVVVVVWpqqqqqqqqqqoqVVVVVVVVVVVVqqqqqqqqqqqqqlRVVVVVVbWqqlpVtaqqVlVVVVVVqaqqqqqqqqoqVVVVVVVVVVVVVVVVVVWVqq" +
        "qqqqqqqqpKVVW1qqqqalVVraqqqqqqqqqqqqqqqqpKVVVVVVVVVVVVVVVVVVVVVVVVVaWqqqqqqqqqqqqqqqpqVVVVq6qqqqqqqqqqqqqqSlVV" +
        "VVVVVVVVVVVVVVVVVVVVVVVVVVWlqqqqqqqqqqqqqqpVVVVVq6qqqqqqqqqqqipVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVqaqqqqqqqqqqql" +
        "VVVVVVq6qqqqqqqqqqUlVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVaqqqqqqqqpqVVVVVVVVtaqqqqqqqlJVVVVVVVVVVVVVVVVVVVVVVVVV" +
        "VVVVVVVVVVVVVVWpqqqqqlVVVVVVVVVVVa2qqqpUVVVVVVVVVVVVVVVVVVVV9Q==");

    public static readonly byte[] DacBlip = Convert.FromBase64String(
        "AAAAQFWrqlXVqqpV1aqqVdWqalXVqmpV1apqVbWqalW1qlpVtapaVbWqWlWtqlpVrapWVa2qVlWtqlZVq6pWVauqVlWrqlVVq6pV1aqqVdWqql" +
        "XVqmpV1apqVbWqalW1qmpVtapaVbWqWlW1qlpVrapaVa2qVlWtqlbV/v8=");
}
