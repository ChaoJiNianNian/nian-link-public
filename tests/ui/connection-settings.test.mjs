import test from 'node:test';
import assert from 'node:assert/strict';
import {createMiniFixture} from './helpers/mini-dom-fixture.mjs';

test('Windows settings exposes the connection and display settings action', () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Windows'});
  const button = fixture.q('[data-open-windows-settings]');
  assert.ok(button);
  assert.equal(button.hidden, false);
  button.onclick();
  assert.equal(Object.keys(fixture.last('connection.openSettings').payload).length, 0);
  assert.equal(fixture.q('[data-mac-settings]').hidden, true);
});

test('Mac retains its own connection settings and hides the Windows entry', () => {
  const fixture = createMiniFixture();
  fixture.state({platform: 'Mac'});
  assert.equal(fixture.q('[data-open-windows-settings]').hidden, true);
  assert.equal(fixture.q('[data-mac-settings]').hidden, false);
  const button = fixture.q('[data-open-mac-settings]');
  assert.ok(button);
  button.onclick();
  assert.equal(Object.keys(fixture.last('connection.openSettings').payload).length, 0);
});
