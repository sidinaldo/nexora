import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { RealtimeServico } from '../nucleo/servicos/realtime.servico';
import { AuthServico } from '../nucleo/servicos/auth.servico';
import { Contatos } from './contatos/contatos';
import { CORPO, RESPONDEM_ARRAY, RealtimeFalso, TELAS, rotaFalsa } from './telas-do-painel';

/** RENDERIZAÇÃO DE CADA TELA DO PAINEL.
 *
 *  ===================== O QUE ESTE ARQUIVO PEGA =====================
 *  Componente e template andam separados e divergem: foi assim que o Meu Dia quebrou — o `.ts`
 *  foi reescrito e o `.html` ficou para trás.
 *
 *  Divergência de TIPO o `ng build` já pega. O que ele NÃO pega é o que só acontece rodando:
 *  binding que compila mas estoura em tempo de execução, `ngOnInit` que lança, provider que
 *  falta, `@for` sobre `undefined`. Nada disso aparece em build; tudo isso aparece aqui.
 *
 *  A checagem é deliberadamente rasa e uniforme — montar, deixar as respostas chegarem, e
 *  exigir que a tela desenhe algo. Não é teste de comportamento de cada página: é a rede que
 *  garante que NENHUMA tela está quebrada no commit, que é exatamente o buraco que existia.
 *  ===================================================================
 *
 *  ===================== A MEDIÇÃO DE CELULAR SAIU DAQUI (MOB-2) =====================
 *  Havia neste arquivo um laço que montava cada tela dentro de um `div` de 380px e media o
 *  transbordo. Ele media o que não dizia medir, e o próprio comentário admitia: MEDIA QUERY
 *  RESPONDE À JANELA do navegador, não à caixa do teste. Com o karma em 1440px, aquilo era o
 *  layout de DESKTOP espremido em 380px.
 *
 *  A consequência não foi teórica. A caixa de entrada precisou ser ISENTA da medição (havia um
 *  `SEM_COBERTURA_A_380PX` só para ela), porque em ≤860px a media query monta outro layout — e
 *  foi exatamente ali que ela quebrou: o painel da conversa saía com `display: none` e tocar num
 *  contato não abria nada. O buraco estava registrado no próprio arquivo e ninguém podia fechá-lo
 *  daqui.
 *
 *  Agora a medição vive em `paginas.celular.spec.ts`, que roda numa janela de 390px de verdade
 *  (ver karma.conf.js) — sem caixa, sem isenção, e sem ressalva. O que sobra aqui é o que não
 *  depende de largura.
 *  ================================================================================== */
describe('renderização das telas', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();

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

    httpMock = TestBed.inject(HttpTestingController);

    // Sessão de dono: o shell e as telas de configuração desenham por papel, e sem sessão
    // metade do template ficaria fora — o teste passaria sem ter olhado para ela.
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: { id: 1, nome: 'Ana Souza', email: 'ana@x.com', papel: 'dono', empresaNome: 'Padaria' }
    } as never);
  });

  afterEach(() => localStorage.clear());

  /** Responde TUDO que a tela pediu, quantas rodadas forem necessárias: uma resposta pode
   *  disparar a próxima requisição (o detalhe do contato carrega a conversa depois do contato). */
  function responderTudo() {
    for (let volta = 0; volta < 5; volta++) {
      const pendentes = httpMock.match(() => true);
      if (pendentes.length === 0) return;
      pendentes.forEach(r =>
        r.flush(RESPONDEM_ARRAY.some(u => r.request.url.includes(u)) ? [] : CORPO));
    }
  }

  for (const tela of TELAS) {
    it(`${tela.nome} monta e desenha sem estourar`, () => {
      const fixture = TestBed.createComponent(tela.componente);

      // Um throw em qualquer um destes passos reprova o teste — é o ponto.
      fixture.detectChanges();     // primeira pintura (estado de carregando)
      responderTudo();
      fixture.detectChanges();     // pintura com os dados

      const html = fixture.nativeElement as HTMLElement;
      expect(html.textContent?.trim().length)
        .withContext('a tela desenhou vazia — template não casou com o componente')
        .toBeGreaterThan(0);
    });
  }

  it('cobre toda tela roteada e todo painel do painel', () => {
    // Guarda contra o esquecimento: tela nova entra na lista, senão o arquivo dá a impressão
    // de cobrir tudo enquanto a última adicionada nunca é montada.
    //
    // A lista mora em `telas-do-painel.ts` e é a MESMA que a suíte de celular percorre — com
    // uma cópia em cada arquivo, a tela nova entraria numa e não na outra, e a que ficou para
    // trás continuaria verde.
    expect(TELAS.length).toBe(22);
  });

  /** ===================== O CAMPO DE BUSCA NÃO PODE ENGOLIR A BARRA =====================
   *  `flex: 1` sem teto fazia a busca dos Contatos ocupar ~60% da linha, e a barra de filtro
   *  passava a ler como uma tela de busca com dois selects de brinde.
   *
   *  A asserção é sobre a PROPORÇÃO, não sobre um número de pixels: um teto fixo em px viraria
   *  falso vermelho na primeira mudança de tipografia. */
  it('a busca dos Contatos não ocupa mais que metade da barra de filtro', () => {
    const caixa = document.createElement('div');
    caixa.style.width = '1100px';
    document.body.appendChild(caixa);

    try {
      const fixture = TestBed.createComponent(Contatos);
      caixa.appendChild(fixture.nativeElement);
      fixture.detectChanges();
      responderTudo();
      fixture.detectChanges();

      const linha = fixture.nativeElement.querySelector('.linha-filtros') as HTMLElement;
      const busca = fixture.nativeElement.querySelector('.linha-filtros .busca') as HTMLElement;

      expect(linha).toBeTruthy();
      expect(busca.getBoundingClientRect().width)
        .withContext('a busca voltou a engolir a barra de filtro')
        .toBeLessThanOrEqual(linha.getBoundingClientRect().width / 2);
    } finally {
      caixa.remove();
    }
  });
});

@Component({ template: '' })
class Vazio { }
