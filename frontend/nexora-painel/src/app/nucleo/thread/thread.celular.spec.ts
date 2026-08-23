import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RealtimeServico } from '../servicos/realtime.servico';
import { AuthServico } from '../servicos/auth.servico';
import { LARGURA_CELULAR, RealtimeFalso } from '../../paginas/telas-do-painel';
import { Thread } from './thread';

/** ===================== O CHIP "NOVA MENSAGEM" (MOB-2) =====================
 *  Ele ficava a 96px do fundo do COMPONENTE — a altura do compositor de desktop, chutada e escrita
 *  como número. Basta o compositor mudar de altura (celular, prévia de anexo, gravação em curso,
 *  campo crescido até cinco linhas) para o chip cair DENTRO do rodapé ou boiar longe dele.
 *
 *  Agora ele se ancora ao fim da ÁREA DE MENSAGENS, e o teste afirma a relação — o chip acima do
 *  compositor — em vez de um número de pixels, que voltaria a mentir na próxima mudança.
 *  ========================================================================== */
describe('thread no celular', () => {
  let http: HttpTestingController;
  let palco: HTMLElement;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);

    palco = document.createElement('div');
    palco.style.width = `${LARGURA_CELULAR}px`;
    palco.style.height = '600px';
    palco.style.display = 'flex';
    document.body.appendChild(palco);

    const f = TestBed.createComponent(Thread);
    f.componentRef.setInput('conversaId', 42);
    f.componentRef.setInput('naoLidas', 0);
    palco.appendChild(f.nativeElement);
    f.detectChanges();

    http.match(() => true).forEach(r => r.flush({
      itens: [
        { id: 1, texto: 'oi', direcao: 'entrada', enviadaEm: '2026-08-05T12:00:00Z', ack: 0, tipoMidia: 'nenhum', erro: null },
        { id: 2, texto: 'tudo bem?', direcao: 'saida', enviadaEm: '2026-08-05T12:01:00Z', ack: 2, tipoMidia: 'nenhum', erro: null }
      ],
      temMais: false
    }));
    f.detectChanges();
    return f;
  }

  afterEach(() => { palco?.remove(); localStorage.clear(); TestBed.resetTestingModule(); });

  it('O CHIP FICA ACIMA DO COMPOSITOR, não por cima dele', () => {
    const f = montar();
    f.componentInstance.temNovaMensagem.set(true);
    f.detectChanges();

    const raiz = f.nativeElement as HTMLElement;
    const chip = raiz.querySelector('.chip-nova') as HTMLElement;
    const compositor = raiz.querySelector('.responder') as HTMLElement;

    expect(chip).withContext('o chip não apareceu').not.toBeNull();

    expect(chip.getBoundingClientRect().bottom)
      .withContext('o chip "nova mensagem" invade o compositor — no celular ele tapava o campo ' +
                   'de escrever ou ficava escondido atrás dele')
      .toBeLessThanOrEqual(compositor.getBoundingClientRect().top + 1);
  });

  it('O COMPOSITOR FICA NO RODAPÉ, com a área de mensagens acima', () => {
    // A thread ocupa a tela inteira no celular; o compositor não pode subir para o meio nem sair
    // por baixo. `100dvh` na cadeia do shell é o que segura isso quando o teclado abre.
    const f = montar();
    const raiz = f.nativeElement as HTMLElement;
    const area = raiz.querySelector('.thread-area') as HTMLElement;
    const compositor = raiz.querySelector('.responder') as HTMLElement;

    expect(compositor.getBoundingClientRect().top)
      .withContext('o compositor não está abaixo das mensagens')
      .toBeGreaterThanOrEqual(area.getBoundingClientRect().bottom - 1);

    expect(Math.round(compositor.getBoundingClientRect().bottom))
      .withContext('o compositor passou do rodapé do painel — ficaria fora da tela')
      .toBeLessThanOrEqual(Math.round(palco.getBoundingClientRect().bottom) + 1);
  });
});
