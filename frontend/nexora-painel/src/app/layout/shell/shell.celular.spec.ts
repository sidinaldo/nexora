import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { RealtimeFalso, rotaFalsa } from '../../paginas/telas-do-painel';
import { Shell } from './shell';

/** ===================== A NAVEGAÇÃO NO CELULAR (MOB-2) ===================== */
describe('barra inferior', () => {
  let http: HttpTestingController;

  /** ⚠️ ASSÍNCRONO: o `ngOnInit` do shell faz `await realtime.conectar()` ANTES de pedir o
   *  status. Sem esperar a microtarefa, `http.match` não encontra requisição nenhuma e a tela
   *  fica no estado inicial — badge zerado e conexão "verificando". */
  async function montar(papel: 'dono' | 'vendedor', status: Partial<Record<string, unknown>> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([{ path: '**', component: Vazio }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana', email: 'a@x.com', papel, empresaNome: 'Padaria' }
    } as never);

    const f = TestBed.createComponent(Shell);
    document.body.appendChild(f.nativeElement);
    f.detectChanges();
    await f.whenStable();

    http.match(() => true).forEach(r => r.flush({
      naoLidas: 0, whatsappConectado: true, trocouDeNumero: false, conexoesCaidas: [],
      mostrar: false, concluidos: 0, total: 0, ...status
    }));
    f.detectChanges();
    await f.whenStable();
    f.detectChanges();
    return f;
  }

  afterEach(() => {
    document.querySelectorAll('app-shell').forEach(e => e.remove());
    localStorage.clear();
    TestBed.resetTestingModule();
  });

  function rotulos(f: { nativeElement: HTMLElement }) {
    return [...f.nativeElement.querySelectorAll('.barra-inferior a')]
      .map(a => (a.textContent ?? '').trim().split(/\s+/)[0]);
  }

  it('A BARRA É IDÊNTICA PARA DONO E VENDEDOR', async () => {
    // ⚠️ O recorte por papel acontece DENTRO do "Mais", nunca aqui. Barra que muda de conteúdo
    // quando o vendedor vira gestor apaga a memória muscular dele: o item do terceiro lugar passa
    // a ser outro, e o dedo erra por semanas.
    const doDono = rotulos(await montar('dono'));
    TestBed.resetTestingModule();
    document.querySelectorAll('app-shell').forEach(e => e.remove());
    const doVendedor = rotulos(await montar('vendedor'));

    expect(doDono).toEqual(['Meu', 'Caixa', 'Funil', 'Contatos', 'Mais']);
    expect(doVendedor)
      .withContext('a barra mudou com o papel — a posição dos itens deixou de ser previsível')
      .toEqual(doDono);
  });

  /** ===================== A BARRA É HORIZONTAL (MOB-3) =====================
   *  Ela nasceu VERTICAL, com os cinco itens empilhados ocupando mais de 40% da altura da tela —
   *  e a suíte do MOB-2 continuou verde, porque afirmava a ALTURA de cada item e nunca a
   *  orientação. Uma barra empilhada satisfaz "cada item tem 56px" com folga.
   *
   *  A causa está registrada em docs/MOB-3.md: `shell.css` declara `nav { flex-direction: column }`
   *  num seletor de ELEMENTO, escrito para a lateral — e a barra inferior também é um `<nav>`.
   *
   *  O teste afirma a GEOMETRIA, não a propriedade CSS: mesma linha e da esquerda para a direita.
   *  Conferir `flex-direction` no estilo computado passaria por qualquer outra forma de empilhar.
   *  ======================================================================== */
  it('A BARRA É HORIZONTAL — cinco itens lado a lado, na mesma linha', async () => {
    const f = await montar('dono');
    const itens = [...(f.nativeElement as HTMLElement).querySelectorAll('.barra-inferior a')]
      .map(a => a.getBoundingClientRect());

    expect(itens.length).toBe(5);

    const topos = new Set(itens.map(r => Math.round(r.top)));
    expect(topos.size)
      .withContext(`os itens estão em ${topos.size} linhas diferentes — a barra está empilhada`)
      .toBe(1);

    for (let i = 1; i < itens.length; i++) {
      expect(itens[i].left)
        .withContext(`o item ${i + 1} não está à direita do anterior`)
        .toBeGreaterThan(itens[i - 1].left);
    }
  });

  it('A BARRA NÃO PASSA DE 64px DE ALTURA', async () => {
    // Numa tela cujo conteúdo é lista de conversas, cada pixel da barra sai da lista.
    const f = await montar('dono');
    const barra = (f.nativeElement as HTMLElement).querySelector('.barra-inferior') as HTMLElement;
    const altura = barra.getBoundingClientRect().height;

    expect(altura)
      .withContext(`a barra mede ${Math.round(altura)}px — está comendo a tela`)
      .toBeLessThanOrEqual(64);
    expect(altura)
      .withContext('a barra ficou baixa demais para o alvo de toque')
      .toBeGreaterThanOrEqual(56);
  });

  it('a lateral do desktop NÃO é renderizada', async () => {
    // Não é `display: none`: ela não existe. Treze links fora da tela não precisam ser montados.
    const f = await montar('dono');
    expect((f.nativeElement as HTMLElement).querySelector('.lateral'))
      .withContext('a barra lateral continua no DOM no celular').toBeNull();
  });

  /** O alvo é o ITEM INTEIRO, não o texto nem o ícone. Um canto que não responde é um toque
   *  perdido — e nesta barra o dedo mira a coluna, não a palavra. */
  it('O ALVO É O ITEM INTEIRO, com 44px de sobra', async () => {
    const f = await montar('dono');
    const itens = [...(f.nativeElement as HTMLElement).querySelectorAll('.barra-inferior a')];
    expect(itens.length).toBe(5);

    for (const i of itens) {
      const r = i.getBoundingClientRect();
      expect(r.height).withContext(`"${i.textContent?.trim()}" ficou abaixo de 44px`)
        .toBeGreaterThanOrEqual(44);
      expect(r.width).withContext(`"${i.textContent?.trim()}" ficou estreito demais`)
        .toBeGreaterThanOrEqual(44);

      // Quatro cantos, 3px para dentro: se algum deles não devolver o item, o alvo é menor do que
      // parece e o toque perto da borda cai no vazio.
      for (const [x, y] of [
        [r.left + 3, r.top + 3], [r.right - 3, r.top + 3],
        [r.left + 3, r.bottom - 3], [r.right - 3, r.bottom - 3]
      ]) {
        expect(i.contains(document.elementFromPoint(Math.round(x), Math.round(y))))
          .withContext(`um canto de "${i.textContent?.trim()}" não responde ao toque`)
          .toBeTrue();
      }
    }
  });

  /** A faixa é do TOPO e a barra é do rodapé: elas não se encontram. O que precisa continuar
   *  valendo é o mecanismo do DES-1 — quem rola é `.conteudo`, e mais ninguém. Com a faixa
   *  aparecendo, é a área de conteúdo que encolhe; com a barra também. */
  it('a faixa de desconexão e a barra não brigam, e a rolagem continua única', async () => {
    const f = await montar('dono', { whatsappConectado: false, conexoesCaidas: ['Vendas'] });
    const raiz = f.nativeElement as HTMLElement;
    const faixa = raiz.querySelector('.banner-alerta') as HTMLElement;
    const conteudo = raiz.querySelector('.conteudo') as HTMLElement;
    const barra = raiz.querySelector('.barra-inferior') as HTMLElement;

    expect(faixa.getBoundingClientRect().bottom)
      .withContext('a faixa não está acima da área de conteúdo').toBeLessThanOrEqual(
        conteudo.getBoundingClientRect().top + 1);
    expect(barra.getBoundingClientRect().top)
      .withContext('a barra invadiu o conteúdo quando a faixa apareceu').toBeGreaterThanOrEqual(
        conteudo.getBoundingClientRect().bottom - 1);

    // Rolagem dupla é o defeito que o DES-1 fechou: só `.conteudo` rola.
    const rolaveis = [...raiz.querySelectorAll('*')]
      .filter(e => {
        const o = getComputedStyle(e as Element).overflowY;
        return o === 'auto' || o === 'scroll';
      })
      .map(e => (e as HTMLElement).className);
    expect(rolaveis)
      .withContext(`mais de um container rolando: ${rolaveis.join(', ')}`)
      .toEqual(['conteudo']);
  });

  /** ===================== NADA FICA ATRÁS DA BARRA =====================
   *  A barra é IRMÃ de `main` num `.app` em coluna, e não `position: fixed`. A diferença importa: em
   *  fluxo ela ENCOLHE a área de conteúdo, e nenhuma tela precisa reservar `padding-bottom` para
   *  ela. Fixa, cada uma das 22 telas teria que compensar a altura — e a que ninguém lembrasse de
   *  compensar ficaria com a última linha atrás da barra, que é o defeito que se está corrigindo.
   *
   *  Uma asserção cobre TODAS as telas porque o container de rolagem é um só (DES-1/DES-2): se a
   *  barra não invade `.conteudo`, não invade nada que seja renderizado dentro dele.
   *  ===================================================================== */
  it('A BARRA NÃO COBRE O CONTEÚDO DE NENHUMA TELA', async () => {
    const f = await montar('dono');
    const raiz = f.nativeElement as HTMLElement;
    const conteudo = raiz.querySelector('.conteudo') as HTMLElement;
    const barra = raiz.querySelector('.barra-inferior') as HTMLElement;

    expect(barra.getBoundingClientRect().top)
      .withContext('a barra começa antes de a área de conteúdo terminar — ela está por cima')
      .toBeGreaterThanOrEqual(conteudo.getBoundingClientRect().bottom - 1);
  });

  /** ⚠️ O DOM é montado à mão porque a regra atravessa DOIS componentes: a barra vive no shell e o
   *  compositor em `nucleo/thread`. O que está sendo afirmado é a regra global contra a estrutura
   *  real que o app produz — `.app` como ancestral, `.responder textarea` como descendente. */
  it('A BARRA SOME ENQUANTO O COMPOSITOR ESTÁ EM FOCO', () => {
    const app = document.createElement('div');
    app.className = 'app';
    app.innerHTML =
      '<footer class="responder"><textarea></textarea></footer>' +
      '<nav class="barra-inferior"><a href="#">Caixa</a></nav>';
    document.body.appendChild(app);

    try {
      const barra = app.querySelector('.barra-inferior') as HTMLElement;
      const campo = app.querySelector('textarea') as HTMLTextAreaElement;

      expect(getComputedStyle(barra).display)
        .withContext('a barra deveria estar visível antes de o campo receber foco').not.toBe('none');

      campo.focus();
      expect(getComputedStyle(barra).display)
        .withContext('a barra continua na tela com o teclado aberto — ela come altura da conversa ' +
                     'e no iPhone fica atrás do teclado, ocupando espaço sem ser tocável')
        .toBe('none');

      campo.blur();
      expect(getComputedStyle(barra).display)
        .withContext('a barra não voltou depois do foco sair').not.toBe('none');
    } finally {
      app.remove();
    }
  });

  it('o BADGE de não lidas continua no item Caixa', async () => {
    // Veio da lateral do DES-3 e não podia se perder: é o que faz o vendedor voltar para a caixa.
    const f = await montar('dono', { naoLidas: 7 });
    const caixa = [...(f.nativeElement as HTMLElement).querySelectorAll('.barra-inferior a')]
      .find(a => a.textContent?.includes('Caixa'))!;
    expect(caixa.querySelector('.badge')?.textContent?.trim()).toBe('7');
  });

  it('o PONTO de status aparece no "Mais" quando a conexão cai, e não quando está tudo bem', async () => {
    // O detalhe fica dentro do "Mais", junto do link de Conexão — mas o SINAL precisa continuar
    // visível de fora, senão o dono só descobre a queda entrando no menu.
    const bem = await montar('dono', { whatsappConectado: true });
    expect((bem.nativeElement as HTMLElement).querySelector('.barra-inferior .ponto-status'))
      .withContext('ponto aceso com o WhatsApp conectado — vira ruído e ninguém olha mais')
      .toBeNull();

    TestBed.resetTestingModule();
    document.querySelectorAll('app-shell').forEach(e => e.remove());

    const caiu = await montar('dono', { whatsappConectado: false, conexoesCaidas: ['Vendas'] });
    expect((caiu.nativeElement as HTMLElement).querySelector('.barra-inferior .ponto-status.caiu'))
      .withContext('o sinal de conexão caída não aparece fora do menu').not.toBeNull();
  });

  it('a FAIXA de WhatsApp desconectado continua impossível de ignorar', async () => {
    const f = await montar('dono', { whatsappConectado: false, conexoesCaidas: ['Vendas'] });
    const faixa = (f.nativeElement as HTMLElement).querySelector('.banner-alerta');
    expect(faixa).withContext('a faixa sumiu no celular').not.toBeNull();
    expect(faixa!.textContent).toContain('não estão sendo enviadas');
  });
});

@Component({ template: '' })
class Vazio { }
