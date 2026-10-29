# Synthetic fixture

Hand-built, not a real NWS capture. It exists because the spring-forward night
(2027-03-13 -> 2027-03-14, 02:00 EST -> 03:00 EDT) is months away.

- Only `forecast-hourly.json` is present, in the same shape as a real capture.
- There is no 02:00 local hour: the `01:00-05:00` period ends at the instant the
  `03:00-04:00` period starts (both are 07:00Z), so every period is exactly one real hour.
- PoP values are planted so tests have known hits (22:00, 01:00, 03:00 and 05:00 at threshold 50).

Replace with a real capture taken around 2027-03-08:

    bash tools/capture-nws.sh dst-springforward-eve
