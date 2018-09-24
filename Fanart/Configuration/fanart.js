define(['loading', 'emby-input', 'emby-button'], function (loading) {
    'use strict';

    function loadPage(page, config) {

        page.querySelector('#txtFanartApiKey').value = config.UserApiKey || '';

        loading.hide();
    }

    function onSubmit(e) {

        e.preventDefault();

        loading.show();

        var form = this;

        ApiClient.getNamedConfiguration("fanart").then(function (config) {

            config.UserApiKey = form.querySelector('#txtFanartApiKey').value;

            ApiClient.updateNamedConfiguration("fanart", config).then(Dashboard.processServerConfigurationUpdateResult);
        });

        // Disable default form submission
        return false;
    }

    return function (view, params) {

        view.querySelector('form').addEventListener('submit', onSubmit);

        view.addEventListener('viewshow', function () {

            loading.show();

            var page = this;

            ApiClient.getNamedConfiguration("fanart").then(function (response) {

                loadPage(page, response);
            });
        });
    };

});
