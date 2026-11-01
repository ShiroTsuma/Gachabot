import { test } from 'node:test';
import assert from 'node:assert/strict';
import { relativeTime } from '../../src/GachaBot.Web/wwwroot/relative-time.mjs';

const now = Date.parse('2026-11-01T12:00:00Z');
for (const [age, expected] of [[0, 'just now'], [59, 'just now'], [60, '1 min ago'], [1500, '25 min ago'], [3600, '1 hr ago'], [86400, '1 day ago'], [172800, '2 days ago'], [-60, 'just now']]) {
    test(`relative time at age ${age} seconds`, () => {
        assert.equal(relativeTime(new Date(now - age * 1000).toISOString(), now), expected);
    });
}
test('invalid timestamps are omitted', () => assert.equal(relativeTime('invalid', now), null));
test('timestamps use their explicit time zone', () => {
    assert.equal(relativeTime('2026-11-01T12:35:00+01:00', now), '25 min ago');
});
