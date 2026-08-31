(() => {
    const bulkForm = document.getElementById('bulk-rent-form');
    if (!bulkForm) return;

    const antiForgeryToken = bulkForm.querySelector('input[name="__RequestVerificationToken"]')?.value;
    if (!antiForgeryToken) return;

    const notice = document.createElement('div');
    notice.setAttribute('role', 'status');
    notice.setAttribute('aria-live', 'polite');
    Object.assign(notice.style, {
        position: 'fixed',
        top: '16px',
        left: '50%',
        transform: 'translateX(-50%)',
        zIndex: '1000',
        display: 'none',
        maxWidth: 'min(720px, calc(100vw - 32px))',
        padding: '11px 16px',
        border: '1px solid #b9d2ff',
        borderRadius: '7px',
        background: '#eef4ff',
        boxShadow: '0 4px 16px rgba(0, 0, 0, .14)',
        color: '#20242a',
        fontWeight: '650'
    });
    document.body.appendChild(notice);

    let noticeTimer;
    const showNotice = (message, isError = false) => {
        notice.textContent = message;
        notice.style.background = isError ? '#fff0f0' : '#eef4ff';
        notice.style.borderColor = isError ? '#e0b4b4' : '#b9d2ff';
        notice.style.display = 'block';
        clearTimeout(noticeTimer);
        noticeTimer = setTimeout(() => {
            notice.style.display = 'none';
        }, 4000);
    };

    const individualActions = new Map([
        ['/discs/reviewed', title => `「${title}」を確認済みに変更しました`],
        ['/discs/rented', title => `「${title}」をレンタル済みに変更しました`],
        ['/discs/reopen', title => `「${title}」を未チェックに変更しました`]
    ]);

    // 個別操作は一括レンタル用formのformactionを利用しているため、そのままsubmitすると
    // 一括操作側の確認ダイアログまで発火する。個別操作だけ先に捕捉して非同期POSTへ切り替える。
    bulkForm.addEventListener('submit', async event => {
        const submitter = event.submitter;
        if (!(submitter instanceof HTMLButtonElement)) return;

        const actionUrl = new URL(submitter.formAction, location.href);
        const messageFactory = individualActions.get(actionUrl.pathname);
        if (!messageFactory) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        submitter.disabled = true;

        const card = submitter.closest('.disc');
        const title = card?.querySelector('.title a')?.textContent?.trim() || 'CD';
        const body = new URLSearchParams({
            id: submitter.value,
            returnUrl: location.pathname + location.search,
            __RequestVerificationToken: antiForgeryToken
        });

        try {
            // 既存POSTエンドポイントのリダイレクト互換性は維持し、画面側だけ非同期化する。
            // fetchはリダイレクト先まで処理するが、そのHTMLは画面へ反映しないためスクロール位置は変わらない。
            const response = await fetch(actionUrl.pathname, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8',
                    'X-Requested-With': 'XMLHttpRequest'
                },
                body
            });

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            showNotice(messageFactory(title));
            card?.remove();

            // カード削除後に選択済みチェックボックスが残っていない場合は一括ボタンを無効化する。
            const bulkButton = document.getElementById('rent-selected');
            if (bulkButton) {
                bulkButton.disabled = !document.querySelector('.disc-select:checked');
            }
        } catch (error) {
            submitter.disabled = false;
            showNotice(`「${title}」の更新に失敗しました`, true);
            console.error('CDの個別状態更新に失敗しました。', error);
        }
    }, true);
})();
