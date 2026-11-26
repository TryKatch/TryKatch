import assert from 'node:assert/strict';
import test from 'node:test';
import { assertEmailLogPrivacy } from '../assert-email-log-privacy.mjs';

test('email diagnostics reject raw, JSON-escaped and URL-encoded private markers without echoing them', () => {
  const markers = ['recipient@private.invalid', 'synthetic-token+/=', 'synthetic-quoted-"token', 'https://app.invalid/reset?token=synthetic-token%2B'];
  for (const marker of markers) {
    for (const value of [marker, JSON.stringify(marker).slice(1, -1), encodeURIComponent(marker)]) {
      assert.throws(() => assertEmailLogPrivacy(`diagnostic ${value}`, markers), error => {
        assert.equal(error.message, 'Private email content was found in API diagnostics.');
        assert.ok(!error.message.includes(marker));
        return true;
      });
    }
  }
  assert.throws(() => assertEmailLogPrivacy('recipient\\u0040private.invalid', markers), /Private email content was found/);
});

test('safe opaque delivery diagnostics pass while missing or invalid private-marker evidence fails closed', () => {
  assert.doesNotThrow(() => assertEmailLogPrivacy('email delivery 01976b71-823a-7443-8ca3-064875a01954 acknowledged', ['recipient@private.invalid']));
  for (const markers of [undefined, [], [''], [42], ['x'.repeat(16_385)]]) {
    assert.throws(() => assertEmailLogPrivacy('safe log', markers), /Private email marker evidence is invalid/);
  }
});
