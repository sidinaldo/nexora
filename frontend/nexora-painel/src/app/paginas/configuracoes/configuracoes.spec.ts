import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { ConfiguracaoEmpresa } from '../../nucleo/modelos';
import { Configuracoes } from './configuracoes';

/** ===================== O LIGA/DESLIGA DA CONCLUSÃO DA VENDA (POS-1) =====================
 *
 *  Esta tela não tinha teste nenhum, e este arquivo não tenta cobri-la: cobre o controle que o
 *  POS-1 acrescentou, que é o único desta tela capaz de desfazer uma decisão do dono em silêncio.
 *
 *  As duas coisas que podem quebrar calado:
 *
 *    1. o campo de dias desaparecer ou PERDER O VALOR quando a chave é desligada. O número é
 *       guardado de propósito — é o que faz religar devolver o prazo que a empresa tinha, em vez
 *       de 7 por acidente;
 *
 *    2. o PUT sair sem o campo. O `CorpoAtendimento` o tem como obrigatório, então omitir não
 *       compila — mas mandar um literal em vez do sinal compila, e o dono salvaria a tela toda
 *       vendo o valor certo na frente dele e outro no banco.
 *  ====================================================================================== */
describe('configurações — a conclusão da venda liga e desliga', () => {
  let fixture: ComponentFixture<Configuracoes>;
  let c: Configuracoes;
  let http: HttpTestingController;

  const CONFIG: ConfiguracaoEmpresa = {
    nome: 'Softio', documento: null, fusoHorario: 'America/Sao_Paulo', uf: 'RN',
    janelaHoraInicio: 8, janelaHoraFim: 20, janelaDiasSemana: 126,
    semaforoAmareloMinutos: 60, semaforoVermelhoMinutos: 240,
    diasSemRespostaFollowUp: 2,
    // 30 e não 7: um valor que a empresa escolheu, para o teste poder provar que ele sobrevive.
    diasParaConcluirVenda: 30,
    conclusaoAutomatica: true
  };

  function montar(sobrepor: Partial<ConfiguracaoEmpresa> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Dona', email: 'd@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Softio'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Configuracoes);
    c = fixture.componentInstance;
    fixture.detectChanges();

    // A tela dispara quatro leituras no `ngOnInit`; só a primeira interessa aqui, e as outras
    // falham de propósito sem derrubar nada (é o comportamento declarado do componente).
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush({ ...CONFIG, ...sobrepor });
      else r.flush([]);
    }
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => http.verify());

  const campoDias = () =>
    fixture.nativeElement.querySelector('#cv') as HTMLInputElement;

  it('carrega LIGADO e com o campo de dias editável', () => {
    montar();

    expect(c.fConclusaoAuto()).toBeTrue();
    expect(c.fDiasConcluir()).toBe(30);
    expect(campoDias().disabled).toBeFalse();
  });

  it('carrega DESLIGADO quando é isso que o servidor diz', async () => {
    // ⚠️ ESTE TESTE EXISTE PORQUE O DE CIMA NÃO PROVA NADA SOZINHO. O sinal nasce `true`, e a
    // configuração padrão também é `true` — então um `carregar()` que simplesmente IGNORASSE a
    // resposta do servidor passaria. Descobri sabotando: apaguei a linha que lê o campo e nenhum
    // teste caiu.
    //
    // Uma empresa que desligou a conclusão abriria a tela vendo o interruptor ligado, e o primeiro
    // "salvar" de qualquer outro campo religaria a feature em silêncio.
    montar({ conclusaoAutomatica: false });
    await fixture.whenStable();

    expect(c.fConclusaoAuto()).toBeFalse();
    expect(campoDias().disabled).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain('Nada é concluído sozinho');
  });

  it('DESLIGAR desabilita o campo de dias e MANTÉM o número', async () => {
    montar();

    c.fConclusaoAuto.set(false);
    fixture.detectChanges();
    // ⚠️ `[disabled]` é escrito no DOM por microtask, não no `detectChanges`. Sem este `await` o
    // teste lê o DOM velho e passa por acidente — já aconteceu neste projeto.
    await fixture.whenStable();

    expect(campoDias().disabled).withContext('o campo fica travado').toBeTrue();
    expect(c.fDiasConcluir()).withContext('mas o valor NÃO se perde').toBe(30);
    expect(campoDias()).withContext('e o campo continua na tela').not.toBeNull();
  });

  it('o aviso troca: desligado, diz que o card fica no quadro e segura a vaga do funil', async () => {
    montar();

    expect(fixture.nativeElement.textContent).toContain('o relógio para');

    c.fConclusaoAuto.set(false);
    fixture.detectChanges();
    await fixture.whenStable();

    const texto = fixture.nativeElement.textContent as string;
    expect(texto).toContain('Nada é concluído sozinho');
    // ⚠️ ESTA FRASE É O PREÇO HONESTO DE DESLIGAR, e esta é a única tela onde alguém o escolhe.
    // Sem ela, o sintoma é o funil deixar de aparecer na hora de abrir um negócio novo, sem
    // nenhuma explicação em lugar nenhum.
    expect(texto).toContain('não pode abrir outro negócio neste funil');
  });

  it('o PUT de atendimento manda a conclusão automática, com o valor da tela', () => {
    montar();

    c.fConclusaoAuto.set(false);
    c.salvarAtendimento();

    const req = http.expectOne(r => r.url.endsWith('/configuracao/atendimento'));

    expect(req.request.body.conclusaoAutomatica)
      .withContext('o valor da tela, não um literal').toBeFalse();
    expect(req.request.body.diasParaConcluirVenda)
      .withContext('e o prazo vai junto, mesmo desligado').toBe(30);

    req.flush(null);
    // O componente recarrega depois de salvar.
    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/configuracao')) r.flush(CONFIG);
      else r.flush([]);
    }
  });
});
