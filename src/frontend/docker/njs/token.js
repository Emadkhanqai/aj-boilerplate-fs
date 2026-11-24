// Google ID tokens for the two Cloud Run services this gateway calls, one per audience.
//
// The audience is the service URL, which is what API_UPSTREAM and AUTH_UPSTREAM already hold, so
// there is no third setting to keep in step with them.
//
// Tokens are minted by js_periodic OFF the request path and cached, because a js_set handler is
// synchronous and cannot call the metadata server. They live about an hour and the refresh runs
// every minute (docker/nginx/upstream/cloud-run/server.conf.template), so a failed refresh keeps
// the previous token for as long as it is still valid.

var HOST = process.env.GCE_METADATA_HOST || '169.254.169.254';
var IDENTITY = '/computeMetadata/v1/instance/service-accounts/default/identity';

// origin only: "https://x.run.app/ignored" -> "https://x.run.app"
function origin(url) {
    var m = /^(https?:\/\/[^\/?#]+)/.exec(url || '');
    return m ? m[1] : '';
}

async function mint(audience) {
    var url = 'http://' + HOST + IDENTITY + '?audience=' + encodeURIComponent(audience) + '&format=full';
    var response = await ngx.fetch(url, { headers: { 'Metadata-Flavor': 'Google' }, timeout: 5000 });
    if (response.status !== 200) {
        throw new Error('metadata server returned ' + response.status);
    }
    var token = (await response.text()).trim();
    if (token.split('.').length !== 3) {
        throw new Error('metadata server returned a malformed token');
    }
    return token;
}

async function refreshAll() {
    var audiences = { api: origin(process.env.API_UPSTREAM), auth: origin(process.env.AUTH_UPSTREAM) };
    var names = ['api', 'auth'];
    for (var i = 0; i < names.length; i++) {
        var key = names[i];
        if (!audiences[key]) {
            ngx.log(ngx.ERR, 'id token: no audience for ' + key + ' (is ' + key.toUpperCase() + '_UPSTREAM set?)');
            continue;
        }
        try {
            var token = await mint(audiences[key]);
            ngx.shared.tokens.set(key, token);
        } catch (e) {
            // A token's value is never logged. The previous one is kept: it is still valid for a while.
            ngx.log(ngx.ERR, 'id token refresh failed for ' + key + ': ' + e.message);
        }
    }
}

function forApi(r) { return ngx.shared.tokens.get('api') || ''; }
function forAuth(r) { return ngx.shared.tokens.get('auth') || ''; }

function ready(r) {
    var missing = ['api', 'auth'].filter(function (k) { return !ngx.shared.tokens.get(k); });
    if (missing.length > 0) {
        r.return(503, 'waiting for id tokens: ' + missing.join(', ') + '\n');
        return;
    }
    r.return(200, 'ready\n');
}

export default { refreshAll, forApi, forAuth, ready };
